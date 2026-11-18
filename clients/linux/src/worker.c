/*
 * The build loop: pre-flight, claim a job, run the renkuports-builder container (or the test build command),
 * upload the packages and complete the job, or fail or release it with the log. Runs in its own thread; it talks
 * to the UI only through the emit callback.
 * MIT License.
 */
#include "builder.h"

#include <errno.h>
#include <fcntl.h>
#include <gio/gunixinputstream.h>
#include <glib/gstdio.h>
#include <json-glib/json-glib.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

//! docker stop's grace (larger than the container's RENKUPORTS_GRACE of 60 s), then docker kill after this long.
static const gint64 kKillAfter = 100 * G_USEC_PER_SEC;
//! How long a stopped build command gets after SIGINT before SIGKILL (as the Haiku client).
static const gint64 kKillGrace = 60 * G_USEC_PER_SEC;

static gboolean
stopping(Worker *w)
{
	return g_atomic_int_get(&w->stop) || g_atomic_int_get(&w->pause);
}

static void
flush(Worker *w)
{
	w->flushed_at = g_get_monotonic_time();
	if (w->pending->len == 0)
		return;
	w->emit(EMIT_LOG, w->pending->str, w->emit_data);
	g_string_truncate(w->pending, 0);
}

static void
output(Worker *w, const char *data, gsize n)
{
	// A write error (full disk) shows up in the upload; the window still gets the text.
	if (w->log_fd >= 0 && write(w->log_fd, data, n) < 0) {
	}
	g_string_append_len(w->pending, data, n);
}

/** A timestamped client message: to stdout, the build log of the current job and the window. */
static void G_GNUC_PRINTF(2, 3)
wlog(Worker *w, const char *format, ...)
{
	va_list args;
	va_start(args, format);
	char *text = g_strdup_vprintf(format, args);
	va_end(args);
	GDateTime *now = g_date_time_new_now_local();
	char *stamp = g_date_time_format(now, "%Y-%m-%d %H:%M:%S");
	char *line = g_strdup_printf("%s %s\n", stamp, text);
	fputs(line, stdout);
	fflush(stdout);
	output(w, line, strlen(line));
	flush(w);
	g_free(line); g_free(stamp); g_free(text);
	g_date_time_unref(now);
}

static void G_GNUC_PRINTF(2, 3)
wstate(Worker *w, const char *format, ...)
{
	va_list args;
	va_start(args, format);
	char *text = g_strdup_vprintf(format, args);
	va_end(args);
	w->emit(EMIT_STATE, text, w->emit_data);
	g_free(text);
}

static void
wsleep(Worker *w, int seconds)
{
	for (int i = 0; i < seconds * 4 && !stopping(w); i++)
		g_usleep(250000);
}

/** Leaves "guest booting" off the job line once the container prints a line that is not its own ("[host] "). */
static void
watch_boot(Worker *w, const char *data, gsize n)
{
	for (gsize i = 0; w->booting && i < n; i++)
		if ((i == 0 || data[i - 1] == '\n') && data[i] != '\n' && strncmp(data + i, "[host] ", MIN(7, n - i))) {
			w->booting = FALSE;
			w->emit(EMIT_JOB, w->job_line, w->emit_data);
		}
}

static void
docker_async(Worker *w, const char *verb, const char *container)
{
	const char *stop[] = {w->docker, "stop", "-t", "90", container, NULL}, *kill[] = {w->docker, "kill", container, NULL};
	GSubprocess *p = g_subprocess_newv(strcmp(verb, "stop") ? kill : stop, G_SUBPROCESS_FLAGS_STDOUT_SILENCE | G_SUBPROCESS_FLAGS_STDERR_SILENCE, NULL);
	g_clear_object(&p);	// GLib reaps it
}

static void
child_setup(gpointer data)
{
	(void)data;
	setpgid(0, 0);
}

static void
child_exited(GObject *source, GAsyncResult *result, gpointer data)
{
	g_subprocess_wait_finish(G_SUBPROCESS(source), result, NULL);
	*(gboolean *)data = TRUE;
}

/**
 * Runs @p argv with stdin from /dev/null and stdout+stderr into the build log and the window (or, with
 * @p capture, into that string only). Stop runs `docker stop -t 90 <container>` (then `docker kill` after 100 s)
 * when @p container is set, otherwise sends the process group SIGINT and SIGKILL after 60 s; captured runs are
 * short helper commands and are not stopped. Returns the exit code (128 + signal if killed, 127 if it could not start).
 */
int
worker_run(Worker *w, GPtrArray *argv, char **env, char **capture, const char *container)
{
	GSubprocessLauncher *launcher = g_subprocess_launcher_new(G_SUBPROCESS_FLAGS_STDOUT_PIPE | G_SUBPROCESS_FLAGS_STDERR_MERGE);
	g_subprocess_launcher_set_child_setup(launcher, child_setup, NULL, NULL);
	for (char **e = env; e && *e; e++) {
		char **kv = g_strsplit(*e, "=", 2);
		g_subprocess_launcher_setenv(launcher, kv[0], kv[1] ? kv[1] : "", TRUE);
		g_strfreev(kv);
	}
	GError *err = NULL;
	GSubprocess *p = g_subprocess_launcher_spawnv(launcher, (const char *const *)argv->pdata, &err);
	g_object_unref(launcher);
	if (p == NULL) {
		if (capture)
			*capture = g_strdup(err->message);
		else
			wlog(w, "cannot run %s: %s", (char *)argv->pdata[0], err->message);
		g_error_free(err);
		return 127;
	}
	GMainContext *ctx = g_main_context_new();
	g_main_context_push_thread_default(ctx);
	gboolean exited = FALSE, reading = TRUE;
	g_subprocess_wait_async(p, NULL, child_exited, &exited);
	int fd = g_unix_input_stream_get_fd(G_UNIX_INPUT_STREAM(g_subprocess_get_stdout_pipe(p)));
	fcntl(fd, F_SETFL, O_NONBLOCK);
	GString *captured = capture ? g_string_new(NULL) : NULL;
	pid_t pid = atoi(g_subprocess_get_identifier(p));
	gint64 stopped_at = 0;
	char buffer[65536];
	while (!exited || reading) {
		if (reading) {
			struct pollfd pf = {fd, POLLIN, 0};
			poll(&pf, 1, 200);
			ssize_t n;
			while ((n = read(fd, buffer, sizeof(buffer))) > 0) {
				if (captured)
					g_string_append_len(captured, buffer, n);
				else {
					output(w, buffer, n);
					watch_boot(w, buffer, n);
				}
			}
			// EOF only means the output closed (a grandchild may still run, or hold it).
			if (n == 0 || (n < 0 && errno != EAGAIN && errno != EINTR))
				reading = FALSE;
		} else
			g_usleep(200000);
		if (exited)
			reading = FALSE;	// whatever the exited process wrote last was drained above
		g_main_context_iteration(ctx, FALSE);
		if (!captured && (w->pending->len > 32768 || g_get_monotonic_time() - w->flushed_at > 300000))
			flush(w);
		if (!exited && !captured && g_atomic_int_get(&w->stop)) {
			gint64 now = g_get_monotonic_time();
			if (stopped_at == 0) {
				stopped_at = now;
				wlog(w, container ? "stopping the container (docker stop -t 90 %s)" : "stopping the build%s",
					container ? container : "");
				if (container)
					docker_async(w, "stop", container);
				else
					kill(-pid, SIGINT);
			} else if (stopped_at > 0 && now - stopped_at > (container ? kKillAfter : kKillGrace)) {
				stopped_at = -1;
				if (container)
					docker_async(w, "kill", container);
				else
					kill(-pid, SIGKILL);
			}
		}
	}
	g_main_context_pop_thread_default(ctx);
	g_main_context_unref(ctx);
	int code = g_subprocess_get_if_exited(p) ? g_subprocess_get_exit_status(p) : 128 + g_subprocess_get_term_sig(p);
	g_object_unref(p);
	if (captured)
		*capture = g_string_free(captured, FALSE);
	else
		flush(w);
	return code;
}

/** Convenience: run a NULL-terminated list of arguments (the docker binary first). */
static int
run_args(Worker *w, char **capture, const char *container, const char *first, ...)
{
	GPtrArray *argv = g_ptr_array_new();
	va_list args;
	va_start(args, first);
	for (const char *a = first; a; a = va_arg(args, const char *))
		g_ptr_array_add(argv, (gpointer)a);
	va_end(args);
	g_ptr_array_add(argv, NULL);
	int code = worker_run(w, argv, NULL, capture, container);
	g_ptr_array_free(argv, TRUE);
	return code;
}

static const char *
norm_arch(const char *a)
{
	return !strcmp(a, "amd64") ? "x86_64" : !strcmp(a, "aarch64") ? "arm64" : a;
}

static gboolean G_GNUC_PRINTF(2, 3)
check_failed(char **error, const char *format, ...)
{
	va_list args;
	va_start(args, format);
	*error = g_strdup_vprintf(format, args);
	va_end(args);
	return FALSE;
}

/**
 * Pre-flight (spec E 2): Docker and its daemon, the image (pulled when @p fetch), `info --json` (arch, targets,
 * guest image, fetched when @p fetch), memory (a warning only) and the cache volume not in use. riscv64 is refused:
 * no riscv64 builder guest exists (spec G 1).
 */
gboolean
preflight(Worker *w, const char *platform, gboolean fetch, GString *report, char **error)
{
	if (!strcmp(platform, "riscv64"))
		return check_failed(error, "riscv64 is not supported: there is no riscv64 builder guest yet; "
			"use this client for arm64, x86_64 or x86");
	g_free(w->docker);
	w->docker = resolve_docker(w->s.docker_path);
	char *out = NULL;
	if (run_args(w, &out, NULL, w->docker, "version", "--format", "{{.Server.Arch}}", NULL) != 0) {
		gboolean r = check_failed(error, "Docker is not available or its daemon is not running (%s): %s",
			w->docker, g_strstrip(out));
		g_free(out);
		return r;
	}
	char *daemon_arch = g_strdup(norm_arch(g_strstrip(out)));
	g_clear_pointer(&out, g_free);
	g_string_append_printf(report, "ok: Docker daemon running (%s, %s)\n", w->docker, daemon_arch);
	gboolean ok = TRUE;
	if (!strcmp(w->s.kvm, "on") && access("/dev/kvm", R_OK | W_OK) != 0) {
		ok = check_failed(error, "kvm=on but /dev/kvm is not available to this user");
		goto done;
	}

	if (run_args(w, &out, NULL, w->docker, "image", "inspect", w->s.image, NULL) != 0) {
		if (!fetch) {
			g_string_append_printf(report, "missing: image %s (Start pulls it)\n", w->s.image);
			goto memory;
		}
		wlog(w, "pulling %s", w->s.image);
		if (run_args(w, NULL, NULL, w->docker, "pull", w->s.image, NULL) != 0) {
			ok = check_failed(error, "cannot pull the image %s", w->s.image);
			goto done;
		}
	}
	g_clear_pointer(&out, g_free);
	g_string_append_printf(report, "ok: image %s\n", w->s.image);

	if (run_args(w, &out, NULL, w->docker, "run", "--rm", w->s.image, "info", "--json", NULL) != 0) {
		ok = check_failed(error, "the image's info command failed: %s", g_strstrip(out));
		goto done;
	}
	JsonParser *parser = json_parser_new();
	JsonNode *root = json_parser_load_from_data(parser, out, -1, NULL) ? json_parser_get_root(parser) : NULL;
	JsonObject *info = root && JSON_NODE_HOLDS_OBJECT(root) ? json_node_get_object(root) : NULL;
	const char *image_arch = info ? json_object_get_string_member_with_default(info, "imageArch", "") : "";
	JsonArray *targets = info && json_object_has_member(info, "targets") ? json_object_get_array_member(info, "targets") : NULL;
	gboolean listed = FALSE;
	for (guint i = 0; targets && i < json_array_get_length(targets); i++)
		listed = listed || !g_strcmp0(json_array_get_string_element(targets, i), platform);
	JsonObject *accel = info && json_object_has_member(info, "accel") ? json_object_get_object_member(info, "accel") : NULL;
	JsonObject *guests = info && json_object_has_member(info, "guestImages") ? json_object_get_object_member(info, "guestImages") : NULL;
	g_free(w->accel);
	w->accel = g_strdup(accel ? json_object_get_string_member_with_default(accel, platform, "tcg") : "tcg");
	const char *guest = guests ? json_object_get_string_member_with_default(guests, platform, "missing") : "missing";
	if (info == NULL)
		ok = check_failed(error, "unreadable info --json from %s: %.300s", w->s.image, out);
	else if (strcmp(norm_arch(image_arch), daemon_arch))
		ok = check_failed(error, "image %s is for %s but the Docker daemon is %s", w->s.image, image_arch, daemon_arch);
	else if (!listed)
		ok = check_failed(error, "image %s cannot build %s", w->s.image, platform);
	else {
		g_string_append_printf(report, "ok: image builds %s (%s)\n", platform,
			!strcmp(w->accel, "kvm") ? "KVM" : "TCG, slow");
		if (!strcmp(guest, "cached"))
			g_string_append_printf(report, "ok: guest image for %s cached\n", platform);
		else if (!fetch)
			g_string_append_printf(report, "missing: guest image for %s (Start downloads it)\n", platform);
		else {
			wlog(w, "downloading the guest image for %s (fetch-images)", platform);
			char *vol = g_strdup_printf("%s:/cache", w->s.cache_volume);
			int code = run_args(w, NULL, "renkuports-prepare", w->docker, "run", "--rm", "--init", "--name",
				"renkuports-prepare", "-v", vol, w->s.image, "fetch-images", platform, NULL);
			g_free(vol);
			if (code != 0)
				ok = check_failed(error, "the guest image for %s could not be prepared (exit %d); run Prepare",
					platform, code);
		}
	}
	g_object_unref(parser);
	g_clear_pointer(&out, g_free);
	if (!ok)
		goto done;

memory:
	if (run_args(w, &out, NULL, w->docker, "info", "--format", "{{.MemTotal}}", NULL) == 0) {
		gint64 need = ((gint64)w->s.guest_mb + 1024) << 20, have = g_ascii_strtoll(out, NULL, 10);
		if (have < need)
			g_string_append_printf(report, "warning: Docker has %" G_GINT64_FORMAT " MiB of memory, a build needs "
				"%" G_GINT64_FORMAT " MiB (lower guest_mb, or raise Memory in Docker Desktop)\n", have >> 20, need >> 20);
		else
			g_string_append_printf(report, "ok: Docker memory %" G_GINT64_FORMAT " MiB\n", have >> 20);
	}
	g_clear_pointer(&out, g_free);
	char *filter = g_strdup_printf("volume=%s", w->s.cache_volume);
	run_args(w, &out, NULL, w->docker, "ps", "--filter", "name=renkuports-job-", "--filter", filter, "--format",
		"{{.Names}}", NULL);
	g_free(filter);
	if (out && *g_strstrip(out))
		ok = check_failed(error, "the cache volume %s is in use by %s; stop it (docker stop -t 90 %s) or use another "
			"cache volume", w->s.cache_volume, out, out);
	else
		g_string_append_printf(report, "ok: cache volume %s free\n", w->s.cache_volume);
done:
	g_free(out);
	g_free(daemon_arch);
	return ok;
}

/** The *.hpkg files in @p dir, without -source.hpkg (the container writes them flat). */
static GPtrArray *
list_hpkgs(const char *dir)
{
	GPtrArray *files = g_ptr_array_new_with_free_func(g_free);
	GDir *d = g_dir_open(dir, 0, NULL);
	for (const char *name; d && (name = g_dir_read_name(d));) {
		char *path = g_build_filename(dir, name, NULL);
		if (g_str_has_suffix(name, ".hpkg") && !g_str_has_suffix(name, "-source.hpkg")
			&& g_file_test(path, G_FILE_TEST_IS_REGULAR))
			g_ptr_array_add(files, path);
		else
			g_free(path);
	}
	if (d)
		g_dir_close(d);
	g_ptr_array_sort(files, (GCompareFunc)g_strcmp0);
	return files;
}

static char *
sha256_file(const char *path)
{
	FILE *f = fopen(path, "rb");
	if (f == NULL)
		return NULL;
	GChecksum *sum = g_checksum_new(G_CHECKSUM_SHA256);
	guchar buffer[65536];
	size_t n;
	while ((n = fread(buffer, 1, sizeof(buffer), f)) > 0)
		g_checksum_update(sum, buffer, n);
	gboolean ok = !ferror(f);
	fclose(f);
	char *hex = ok ? g_strdup(g_checksum_get_string(sum)) : NULL;
	g_checksum_free(sum);
	return hex;
}

typedef struct {
	const char *head, *path, *tail;
	goffset size;
} Upload;

static GInputStream *
upload_body(gpointer data, goffset *size, char **type, char **error)
{
	Upload *u = data;
	GError *err = NULL;
	GInputStream *s = multipart_stream(u->head, u->path, u->tail, &err);
	if (s == NULL) {
		*error = g_strdup_printf("cannot open %s: %s", u->path, err->message);
		g_error_free(err);
		return NULL;
	}
	*size = u->size;
	// The boundary is the first line of the head, after "--".
	*type = g_strdup_printf("multipart/form-data; boundary=%.*s", (int)strcspn(u->head + 2, "\r"), u->head + 2);
	return s;
}

/** Uploads one package; NULL on success, else the failure text. */
static char *
upload(Worker *w, gint64 id, const char *path)
{
	char *file = g_path_get_basename(path), *sha = sha256_file(path), *result = NULL;
	GStatBuf st;
	if (sha == NULL || g_stat(path, &st) != 0) {
		result = g_strdup_printf("cannot hash %s", file);
		goto out;
	}
	GDateTime *mtime = g_date_time_new_from_unix_utc(st.st_mtime);
	char *date = g_date_time_format(mtime, "%Y-%m-%dT%H:%M:%SZ");
	g_date_time_unref(mtime);
	char *boundary = g_strdup_printf("renkuports-%" G_GINT64_MODIFIER "x", g_get_real_time());
	char *head = multipart_head(boundary, sha, date, file), *tail = multipart_tail(boundary);
	Upload u = {head, path, tail, (goffset)(strlen(head) + st.st_size + strlen(tail))};
	wlog(w, "uploading %s (%" G_GINT64_FORMAT " bytes, sha256 %s)", file, (gint64)st.st_size, sha);
	char *api_path = g_strdup_printf("api/client/jobs/%" G_GINT64_FORMAT "/packages", id), *error = NULL;
	guint status;
	if (api_call(&w->api, "POST", api_path, upload_body, &u, &status, NULL, &error))
		wlog(w, "uploaded %s", file);
	else
		result = g_strconcat("upload failed: ", error, NULL);
	g_free(error); g_free(api_path); g_free(head); g_free(tail); g_free(boundary); g_free(date);
out:
	g_free(sha);
	g_free(file);
	return result;
}

static void
rm_rf(Worker *w, const char *path)
{
	char *out = NULL;
	run_args(w, &out, NULL, "rm", "-rf", "--", path, NULL);
	g_free(out);
}

static char *
member(JsonObject *o, const char *name)
{
	return g_strdup(json_object_get_string_member_with_default(o, name, ""));
}

/** The ports repository from /api/client/me (portsRepo is null when the server reads a local tree). */
static void
ports_repo(Worker *w, Job *job)
{
	guint status;
	GBytes *reply = NULL;
	char *error = NULL;
	if (api_call(&w->api, "GET", "api/client/me", NULL, NULL, &status, &reply, &error)) {
		JsonParser *parser = json_parser_new();
		gsize n;
		const char *data = g_bytes_get_data(reply, &n);
		JsonNode *root = json_parser_load_from_data(parser, data, n, NULL) ? json_parser_get_root(parser) : NULL;
		JsonObject *me = root && JSON_NODE_HOLDS_OBJECT(root) ? json_node_get_object(root) : NULL;
		JsonNode *repo = me ? json_object_get_member(me, "portsRepo") : NULL;
		if (repo && JSON_NODE_HOLDS_OBJECT(repo)) {
			job->ports_url = member(json_node_get_object(repo), "url");
			job->ports_ref = g_strdup(json_object_get_string_member_with_default(json_node_get_object(repo), "branch", "main"));
		}
		g_object_unref(parser);
		g_bytes_unref(reply);
	} else
		wlog(w, "warning: cannot read api/client/me (%s)", error);
	g_free(error);
}

static void
process_job(Worker *w, JsonObject *o)
{
	Job job = {(gint64)json_object_get_double_member_with_default(o, "jobId", 0), member(o, "portName"),
		member(o, "version"), member(o, "platform"), member(o, "recipePath"), NULL, NULL};
	char *name = g_strdup_printf("%s-%s", job.port, job.version);
	gboolean fake = *w->s.build_command != '\0';
	char *tag = strrchr(w->s.image, ':');
	tag = tag && !strchr(tag, '/') ? tag + 1 : w->s.image;
	g_free(w->job_line);
	w->job_line = fake ? g_strdup_printf("%s (job %" G_GINT64_FORMAT ")", name, job.id)
		: g_strdup_printf("%s (job %" G_GINT64_FORMAT ") for %s, %s, image %s", name, job.id, job.platform,
			!g_strcmp0(w->accel, "kvm") ? "KVM" : "TCG (slow)", tag);
	w->booting = !fake;
	char *booting = g_strconcat(w->job_line, ", guest booting", NULL);
	w->emit(EMIT_JOB, fake ? w->job_line : booting, w->emit_data);
	g_free(booting);
	w->emit(EMIT_LOG_CLEAR, NULL, w->emit_data);
	wstate(w, "building %s", name);

	char *id = g_strdup_printf("%" G_GINT64_FORMAT, job.id), *dirname = g_strconcat("job-", id, NULL);
	char *dir = g_build_filename(w->s.work_dir, dirname, NULL), *outdir = g_build_filename(dir, "out", NULL),
		*log_path = g_build_filename(dir, "build.log", NULL);
	rm_rf(w, dir);
	g_mkdir_with_parents(outdir, 0755);
	w->log_fd = open(log_path, O_WRONLY | O_CREAT | O_TRUNC | O_APPEND, 0644);
	// "build": the job's build number, from servers that verify each job with two matching builds.
	char *build = json_object_has_member(o, "build")
		? g_strdup_printf(", build %d", (int)json_object_get_double_member(o, "build")) : g_strdup("");
	wlog(w, "job %s: building %s for %s (recipe %s%s)", id, name, job.platform, job.recipe, build);
	g_free(build);

	JobAction action = ACTION_FAIL;
	char *summary = NULL;
	if (fake) {
		char *env[] = {g_strconcat("RENKUPORTS_PLATFORM=", job.platform, NULL), g_strconcat("RENKUPORTS_JOB_ID=", id, NULL),
			g_strconcat("RENKUPORTS_RECIPE_PATH=", job.recipe, NULL), NULL};
		GPtrArray *argv = g_ptr_array_new();
		g_ptr_array_add(argv, w->s.build_command);
		g_ptr_array_add(argv, job.port);
		g_ptr_array_add(argv, job.version);
		g_ptr_array_add(argv, outdir);
		g_ptr_array_add(argv, NULL);
		wlog(w, "running: %s %s %s %s", w->s.build_command, job.port, job.version, outdir);
		int code = worker_run(w, argv, env, NULL, NULL);
		g_ptr_array_free(argv, TRUE);
		for (int i = 0; env[i]; i++)
			g_free(env[i]);
		if (g_atomic_int_get(&w->stop))
			action = ACTION_RELEASE, summary = g_strdup("client stopped");
		else if (code != 0)
			summary = g_strdup_printf("build command exited with code %d", code);
		else
			action = ACTION_COMPLETE;
	} else {
		ports_repo(w, &job);
		char *container = container_name(job.id);
		if (job.ports_url == NULL || !*job.ports_url)
			summary = g_strdup("the server has no ports repository (portsRepo), so the container cannot get the "
				"ports tree");
		else if (!strcmp(w->s.pull_policy, "always") && run_args(w, NULL, NULL, w->docker, "pull", w->s.image, NULL) != 0)
			action = ACTION_RELEASE, summary = g_strdup_printf("docker pull %s failed", w->s.image);
		else {
			GPtrArray *args = docker_run_argv(&w->s, &job, outdir, access("/dev/kvm", R_OK | W_OK) == 0);
			g_ptr_array_insert(args, 0, g_strdup(w->docker));
			char *line = g_strjoinv(" ", (char **)args->pdata);
			for (int attempt = 0; attempt < 2; attempt++) {
				wlog(w, "running: %s", line);
				int code = worker_run(w, args, NULL, NULL, container);
				g_clear_pointer(&summary, g_free);
				action = exit_action(code, g_atomic_int_get(&w->stop), &summary);
				if (action != ACTION_RETRY)
					break;
				// Exit 4: the lock is held, which this client should prevent; wait and retry once, then give it back.
				action = ACTION_RELEASE;
				if (attempt == 0) {
					wlog(w, "%s; retrying in 30 s", summary);
					wsleep(w, 30);
					if (g_atomic_int_get(&w->stop)) {
						g_free(summary);
						summary = g_strdup("client stopped");
						break;
					}
				}
			}
			g_free(line);
			g_ptr_array_free(args, TRUE);
		}
		g_free(container);
	}

	GPtrArray *files = list_hpkgs(outdir);
	if (action == ACTION_COMPLETE && files->len == 0)
		action = ACTION_FAIL, summary = g_strdup("build produced no .hpkg");
	for (guint i = 0; action == ACTION_COMPLETE && i < files->len; i++) {
		summary = upload(w, job.id, files->pdata[i]);
		if (g_atomic_int_get(&w->stop))
			action = ACTION_RELEASE, g_free(summary), summary = g_strdup("client stopped");
		else if (summary)
			action = ACTION_FAIL;
	}
	// Reports must go out after Stop too (Stop cancelled the request in flight), so the job is not left claimed.
	g_cancellable_reset(w->api.cancel);
	GString *log = read_log_tail(log_path);
	char *result = NULL, *error = NULL;
	if (action == ACTION_COMPLETE) {
		char *text = json_string(log->str, log->len), *json = g_strdup_printf("{\"log\":%s}", text);
		char *path = g_strdup_printf("api/client/jobs/%s/complete", id);
		guint status;
		GBytes *reply = NULL;
		if (api_post_json(&w->api, path, json, &status, &reply, &error)) {
			// Succeeded, or Verifying while the server waits for a second matching build.
			JsonParser *parser = json_parser_new();
			gsize n;
			const char *data = g_bytes_get_data(reply, &n);
			JsonNode *root = json_parser_load_from_data(parser, data, n, NULL) ? json_parser_get_root(parser) : NULL;
			const char *st = root && JSON_NODE_HOLDS_OBJECT(root)
				? json_object_get_string_member_with_default(json_node_get_object(root), "status", "?") : "?";
			wlog(w, "job %s: completed, %u package(s) uploaded, job is %s", id, files->len, st);
			result = g_strdup_printf("%s (job %s): completed, %u package(s), job %s", name, id, files->len, st);
			g_object_unref(parser);
			g_bytes_unref(reply);
		} else
			action = ACTION_FAIL, summary = g_strconcat("upload failed: ", error, NULL);
		g_clear_pointer(&error, g_free);
		g_free(text); g_free(json); g_free(path);
	}
	if (action == ACTION_RELEASE) {
		wlog(w, "job %s: giving it back: %s", id, summary);
		g_string_free(log, TRUE);
		log = read_log_tail(log_path);
		if (!api_release(&w->api, job.id, summary, log, &error))
			wlog(w, "job %s: releasing failed (%s); the job stays claimed until its lease expires", id, error);
		result = g_strdup_printf("%s (job %s): given back: %s", name, id, summary);
	} else if (action != ACTION_COMPLETE) {
		wlog(w, "job %s: failed: %s", id, summary);
		g_string_free(log, TRUE);
		log = read_log_tail(log_path);
		if (!api_fail(&w->api, job.id, summary, log, &error))
			wlog(w, "job %s: reporting the failure failed (%s); the job stays claimed until its lease expires", id, error);
		result = g_strdup_printf("%s (job %s): failed: %s", name, id, summary);
		if (action == ACTION_FAIL_PAUSE)
			g_atomic_int_set(&w->pause, 1);
	}
	w->emit(EMIT_RESULT, result, w->emit_data);
	g_free(result); g_free(error);
	g_string_free(log, TRUE);
	close(w->log_fd);
	w->log_fd = -1;
	rm_rf(w, outdir);	// hpkgs can be large; build.log stays
	g_ptr_array_free(files, TRUE);
	g_free(summary); g_free(id); g_free(dirname); g_free(dir); g_free(outdir); g_free(log_path); g_free(name);
	g_free(job.port); g_free(job.version); g_free(job.platform); g_free(job.recipe);
	g_free(job.ports_url); g_free(job.ports_ref);
}

static gpointer
loop(gpointer data)
{
	Worker *w = data;
	api_init(&w->api, w->s.server, w->s.client_id, w->s.secret, w->cancel);
	gboolean fake = *w->s.build_command != '\0', checked = fake, refused = FALSE;
	g_free(w->docker);
	w->docker = resolve_docker(w->s.docker_path);
	wlog(w, "RenkuPorts Builder %s: polling %s every %d s%s", APP_VERSION, w->s.server, w->s.poll_seconds,
		fake ? " (test build command)" : "");
	while (!stopping(w)) {
		wstate(w, "connecting");
		char *summary = NULL, *platform = NULL, *error = NULL;
		gboolean known = api_summary(&w->api, &summary, &platform, &error);
		if (known)
			w->emit(EMIT_QUEUE, summary, w->emit_data);
		g_free(summary);
		if (!checked && !known) {
			wstate(w, "error: %s", error);
			wlog(w, "error: %s", error);
			wsleep(w, w->s.poll_seconds);
		} else if (!checked) {
			// Pre-flight once per Start (settings only change across Stop and Start), before anything is claimed.
			wstate(w, "checking Docker");
			GString *report = g_string_new(NULL);
			if ((checked = preflight(w, platform, TRUE, report, &error)))
				wlog(w, "pre-flight:\n%s", report->str);
			else {
				wlog(w, "pre-flight failed: %s", error);
				wstate(w, "paused: %s", error);
				g_atomic_int_set(&w->pause, 1);
				refused = TRUE;
			}
			g_string_free(report, TRUE);
		}
		g_free(platform);
		if (!checked) {
			g_free(error);
			continue;
		}
		g_clear_pointer(&error, g_free);
		guint status;
		GBytes *reply = NULL;
		if (!api_call(&w->api, "POST", "api/client/jobs/next", NULL, NULL, &status, &reply, &error)) {
			if (!g_atomic_int_get(&w->stop)) {
				wstate(w, "error: %s", error);
				wlog(w, "error: %s", error);
				wsleep(w, w->s.poll_seconds);
			}
		} else if (status == 204) {
			wstate(w, "connected, idle (no job queued)");
			if (!w->once)
				wsleep(w, w->s.poll_seconds);
		} else {
			JsonParser *parser = json_parser_new();
			gsize n;
			const char *text = g_bytes_get_data(reply, &n);
			JsonNode *root = json_parser_load_from_data(parser, text, n, NULL) ? json_parser_get_root(parser) : NULL;
			JsonObject *job = root && JSON_NODE_HOLDS_OBJECT(root) ? json_node_get_object(root) : NULL;
			if (job && json_object_has_member(job, "jobId")) {
				process_job(w, job);
				w->emit(EMIT_JOB, "", w->emit_data);
			} else {
				wstate(w, "error: unreadable job");
				wlog(w, "error: unreadable reply to jobs/next: %.*s", (int)MIN(n, 300), text);
				wsleep(w, w->s.poll_seconds);
			}
			g_object_unref(parser);
		}
		g_clear_pointer(&reply, g_bytes_unref);
		g_free(error);
		if (w->once)
			g_atomic_int_set(&w->pause, 1);
	}
	gboolean stopped = g_atomic_int_get(&w->stop);
	wlog(w, stopped ? "stopped" : "paused");
	if (stopped || !refused)
		wstate(w, stopped ? "stopped" : "paused");
	api_clear(&w->api);
	w->emit(EMIT_EXITED, NULL, w->emit_data);
	return NULL;
}

void
worker_init(Worker *w, EmitFunc emit, gpointer data)
{
	memset(w, 0, sizeof(*w));
	settings_init(&w->s);
	w->emit = emit;
	w->emit_data = data;
	w->log_fd = -1;
	w->pending = g_string_new(NULL);
	w->cancel = g_cancellable_new();
}

void
worker_clear(Worker *w)
{
	settings_clear(&w->s);
	g_string_free(w->pending, TRUE);
	g_object_unref(w->cancel);
	g_free(w->docker);
	g_free(w->accel);
	g_free(w->job_line);
}

void
worker_start(Worker *w, const Settings *s)
{
	g_atomic_int_set(&w->pause, 0);
	if (w->thread)
		return;
	settings_clear(&w->s);
	settings_copy(&w->s, s);
	g_atomic_int_set(&w->stop, 0);
	g_cancellable_reset(w->cancel);
	w->thread = g_thread_new("build worker", loop, w);
}

void
worker_pause(Worker *w)
{
	g_atomic_int_set(&w->pause, 1);
}

void
worker_stop(Worker *w)
{
	g_atomic_int_set(&w->stop, 1);
	// The worker thread sees the flag and runs docker stop itself; this only aborts a request in flight.
	g_cancellable_cancel(w->cancel);
}

gboolean
worker_running(Worker *w)
{
	return w->thread != NULL;
}

void
worker_join(Worker *w)
{
	if (w->thread)
		g_thread_join(w->thread);
	w->thread = NULL;
}
