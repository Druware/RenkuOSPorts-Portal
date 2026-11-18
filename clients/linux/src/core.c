/*
 * Container contract helpers (spec A): the docker run line, the exit code mapping, and the multipart upload body.
 * MIT License.
 */
#include "builder.h"

#include <string.h>
#include <unistd.h>

/* A read-only stream over several streams in turn, so the upload is streamed from disk with its fields first. */
typedef struct {
	GInputStream parent;
	GInputStream *parts[3];
	int current;
} ConcatStream;
typedef GInputStreamClass ConcatStreamClass;
G_DEFINE_TYPE(ConcatStream, concat_stream, G_TYPE_INPUT_STREAM)

static gssize
concat_read(GInputStream *stream, void *buffer, gsize count, GCancellable *cancel, GError **error)
{
	ConcatStream *self = (ConcatStream *)stream;
	while (self->current < 3) {
		gssize n = g_input_stream_read(self->parts[self->current], buffer, count, cancel, error);
		if (n != 0)
			return n;	// data or an error
		self->current++;
	}
	return 0;
}

static gboolean
concat_close(GInputStream *stream, GCancellable *cancel, GError **error)
{
	(void)error;	// closing the parts cannot lose data: the stream is read-only
	ConcatStream *self = (ConcatStream *)stream;
	for (int i = 0; i < 3; i++)
		g_input_stream_close(self->parts[i], cancel, NULL);
	return TRUE;
}

static void
concat_finalize(GObject *object)
{
	ConcatStream *self = (ConcatStream *)object;
	for (int i = 0; i < 3; i++)
		g_clear_object(&self->parts[i]);
	G_OBJECT_CLASS(concat_stream_parent_class)->finalize(object);
}

static void
concat_stream_class_init(ConcatStreamClass *klass)
{
	klass->read_fn = concat_read;
	klass->close_fn = concat_close;
	G_OBJECT_CLASS(klass)->finalize = concat_finalize;
}

static void
concat_stream_init(ConcatStream *self)
{
	(void)self;
}

char *
multipart_head(const char *boundary, const char *sha, const char *date, const char *filename)
{
	// The server reads sha256 and buildDate before the file part, so they come first.
	return g_strdup_printf("--%s\r\nContent-Disposition: form-data; name=\"sha256\"\r\n\r\n%s\r\n"
		"--%s\r\nContent-Disposition: form-data; name=\"buildDate\"\r\n\r\n%s\r\n"
		"--%s\r\nContent-Disposition: form-data; name=\"file\"; filename=\"%s\"\r\n"
		"Content-Type: application/octet-stream\r\n\r\n", boundary, sha, boundary, date, boundary, filename);
}

char *
multipart_tail(const char *boundary)
{
	return g_strdup_printf("\r\n--%s--\r\n", boundary);
}

GInputStream *
multipart_stream(const char *head, const char *path, const char *tail, GError **error)
{
	GFile *file = g_file_new_for_path(path);
	GFileInputStream *in = g_file_read(file, NULL, error);
	g_object_unref(file);
	if (in == NULL)
		return NULL;
	ConcatStream *self = g_object_new(concat_stream_get_type(), NULL);
	self->parts[0] = g_memory_input_stream_new_from_data(g_strdup(head), strlen(head), g_free);
	self->parts[1] = G_INPUT_STREAM(in);
	self->parts[2] = g_memory_input_stream_new_from_data(g_strdup(tail), strlen(tail), g_free);
	return G_INPUT_STREAM(self);
}

/**
 * Maps the container's exit code (spec A) to what the client does. Infrastructure failures and Stop give the job
 * back (release) instead of burning it; exit 2 is a client bug, so it also pauses the client.
 */
JobAction
exit_action(int code, gboolean stopped, char **summary)
{
	if (stopped || code == 75) {
		*summary = g_strdup("client stopped");
		return ACTION_RELEASE;
	}
	switch (code) {
	case 0:
		*summary = NULL;
		return ACTION_COMPLETE;
	case 2:
		*summary = g_strdup("the container rejected its arguments (exit 2); client paused");
		return ACTION_FAIL_PAUSE;
	case 3:
		*summary = g_strdup("build environment failed (exit 3)");
		return ACTION_RELEASE;
	case 4:
		*summary = g_strdup("cache volume busy (exit 4)");
		return ACTION_RETRY;
	case 5:
		*summary = g_strdup("guest image missing or invalid (exit 5); run Prepare");
		return ACTION_RELEASE;
	case 125: case 126: case 127: case 137:
		*summary = g_strdup_printf("docker failed or the container was killed (exit %d)", code);
		return ACTION_RELEASE;
	default:
		*summary = g_strdup_printf("build failed (exit %d)", code);
		return ACTION_FAIL;
	}
}

char *
container_name(gint64 job_id)
{
	return g_strdup_printf("renkuports-job-%" G_GINT64_FORMAT, job_id);
}

static void
add(GPtrArray *a, const char *s)
{
	g_ptr_array_add(a, g_strdup(s));
}

static void
add_env(GPtrArray *a, const char *name, const char *value)
{
	add(a, "-e");
	g_ptr_array_add(a, g_strdup_printf("%s=%s", name, value ? value : ""));
}

GPtrArray *
docker_run_argv(const Settings *s, const Job *job, const char *outdir, gboolean kvm_present)
{
	GPtrArray *a = g_ptr_array_new_with_free_func(g_free);
	char *name = container_name(job->id);
	add(a, "run"); add(a, "--rm"); add(a, "--init"); add(a, "--name"); add(a, name);
	g_free(name);
	gboolean on = !strcmp(s->kvm, "on"), off = !strcmp(s->kvm, "off");
	if (on || (!off && kvm_present)) {
		add(a, "--device");
		add(a, "/dev/kvm");
	}
	add(a, "-v"); g_ptr_array_add(a, g_strdup_printf("%s:/cache", s->cache_volume));
	add(a, "-v"); g_ptr_array_add(a, g_strdup_printf("%s:/out", outdir));
	char *id = g_strdup_printf("%" G_GINT64_FORMAT, job->id), *jobs = g_strdup_printf("%d", s->jobs),
		*mb = g_strdup_printf("%d", s->guest_mb);
	add_env(a, "RENKUPORTS_JOB_ID", id);
	add_env(a, "RENKUPORTS_PLATFORM", job->platform);
	add_env(a, "RENKUPORTS_RECIPE_PATH", job->recipe);
	add_env(a, "RENKUPORTS_PORTS_URL", job->ports_url);
	add_env(a, "RENKUPORTS_PORTS_REF", job->ports_ref);
	add_env(a, "RENKUPORTS_JOBS", jobs);
	add_env(a, "RENKUPORTS_GUEST_MB", mb);
	add_env(a, "RENKUPORTS_ACCEL", on ? "kvm" : off ? "tcg" : "auto");
	g_free(id); g_free(jobs); g_free(mb);
	add(a, s->image); add(a, "build"); add(a, job->platform); add(a, job->port); add(a, job->version);
	g_ptr_array_add(a, NULL);
	return a;
}

char *
resolve_docker(const char *docker_path)
{
	if (strchr(docker_path, '/'))
		return g_strdup(docker_path);
	char *found = g_find_program_in_path(docker_path);
	if (found)
		return found;
	// A GUI app started from a desktop menu may not have the shell's PATH.
	const char *home = g_get_home_dir();
	char *dirs[] = {g_strdup("/usr/local/bin"), g_strdup("/usr/bin"), g_strdup("/snap/bin"),
		g_build_filename(home, ".local", "bin", NULL), g_build_filename(home, "bin", NULL)};
	for (gsize i = 0; i < G_N_ELEMENTS(dirs); i++) {
		char *path = g_build_filename(dirs[i], docker_path, NULL);
		if (!found && g_file_test(path, G_FILE_TEST_IS_EXECUTABLE))
			found = g_strdup(path);
		g_free(path);
		g_free(dirs[i]);
	}
	return found ? found : g_strdup(docker_path);
}
