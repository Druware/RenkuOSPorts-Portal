/*
 * Settings file and small text helpers.
 * MIT License.
 */
#include "builder.h"

#include <errno.h>
#include <fcntl.h>
#include <glib/gstdio.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

void
settings_init(Settings *s)
{
	memset(s, 0, sizeof(*s));
	s->server = g_strdup("http://localhost:8080/");
	s->client_id = g_strdup("");
	s->secret = g_strdup("");
	s->docker_path = g_strdup("docker");
	s->image = g_strdup(DEFAULT_IMAGE);
	s->kvm = g_strdup("auto");
	s->cache_volume = g_strdup("renkuports-cache");
	s->pull_policy = g_strdup("if-missing");
	s->work_dir = g_build_filename(g_get_user_data_dir(), "renkuports-builder", "work", NULL);
	s->build_command = g_strdup("");
	s->jobs = 4;
	s->guest_mb = 8192;
	s->poll_seconds = 60;
}

void
settings_clear(Settings *s)
{
	char **fields[] = {&s->server, &s->client_id, &s->secret, &s->docker_path, &s->image, &s->kvm,
		&s->cache_volume, &s->pull_policy, &s->work_dir, &s->build_command};
	for (gsize i = 0; i < G_N_ELEMENTS(fields); i++)
		g_clear_pointer(fields[i], g_free);
}

void
settings_copy(Settings *to, const Settings *from)
{
	*to = *from;
	to->server = g_strdup(from->server);
	to->client_id = g_strdup(from->client_id);
	to->secret = g_strdup(from->secret);
	to->docker_path = g_strdup(from->docker_path);
	to->image = g_strdup(from->image);
	to->kvm = g_strdup(from->kvm);
	to->cache_volume = g_strdup(from->cache_volume);
	to->pull_policy = g_strdup(from->pull_policy);
	to->work_dir = g_strdup(from->work_dir);
	to->build_command = g_strdup(from->build_command);
}

char *
settings_path(void)
{
	return g_build_filename(g_get_user_config_dir(), "renkuports-builder", "settings", NULL);
}

static void
set(char **field, const char *value)
{
	g_free(*field);
	*field = g_strdup(value);
}

gboolean
settings_load(Settings *s, const char *path)
{
	char *text;
	if (!g_file_get_contents(path, &text, NULL, NULL))
		return FALSE;
	char **lines = g_strsplit(text, "\n", -1);
	for (char **l = lines; *l; l++) {
		g_strchomp(*l);	// \r from an edited file
		char *eq = strchr(*l, '=');
		if (eq == NULL || **l == '#')
			continue;
		*eq = '\0';
		const char *key = *l, *value = eq + 1;
		if (!strcmp(key, "server")) set(&s->server, value);
		else if (!strcmp(key, "client_id")) set(&s->client_id, value);
		else if (!strcmp(key, "secret")) set(&s->secret, value);
		else if (!strcmp(key, "docker_path")) set(&s->docker_path, value);
		else if (!strcmp(key, "image")) set(&s->image, value);
		else if (!strcmp(key, "kvm")) set(&s->kvm, value);
		else if (!strcmp(key, "cache_volume")) set(&s->cache_volume, value);
		else if (!strcmp(key, "pull_policy")) set(&s->pull_policy, value);
		else if (!strcmp(key, "work_dir")) set(&s->work_dir, value);
		else if (!strcmp(key, "build_command")) set(&s->build_command, value);
		else if (!strcmp(key, "jobs")) s->jobs = atoi(value);
		else if (!strcmp(key, "guest_mb")) s->guest_mb = atoi(value);
		else if (!strcmp(key, "poll_seconds")) s->poll_seconds = atoi(value);
		else if (!strcmp(key, "run_on_start")) s->run_on_start = !strcmp(value, "true");
	}
	g_strfreev(lines);
	g_free(text);
	s->jobs = MAX(s->jobs, 1);
	s->guest_mb = MAX(s->guest_mb, 512);
	s->poll_seconds = MAX(s->poll_seconds, 5);
	return TRUE;
}

static void
line(GString *out, const char *key, const char *value)
{
	// Values are single lines; strip newlines a paste may have brought in.
	g_string_append_printf(out, "%s=", key);
	for (const char *c = value; *c; c++)
		if (*c != '\n' && *c != '\r')
			g_string_append_c(out, *c);
	g_string_append_c(out, '\n');
}

/** Writes a temporary file created with mode 0600 and renames it over the old one, so the secret is never world-readable. */
gboolean
settings_save(const Settings *s, const char *path)
{
	char *dir = g_path_get_dirname(path);
	g_mkdir_with_parents(dir, 0700);
	g_free(dir);
	GString *out = g_string_new(NULL);
	line(out, "server", s->server);
	line(out, "client_id", s->client_id);
	line(out, "secret", s->secret);
	line(out, "docker_path", s->docker_path);
	line(out, "image", s->image);
	line(out, "kvm", s->kvm);
	line(out, "cache_volume", s->cache_volume);
	line(out, "pull_policy", s->pull_policy);
	line(out, "work_dir", s->work_dir);
	g_string_append_printf(out, "jobs=%d\nguest_mb=%d\nrun_on_start=%s\n", s->jobs, s->guest_mb,
		s->run_on_start ? "true" : "false");
	if (*s->build_command)
		line(out, "build_command", s->build_command);
	if (s->poll_seconds != 60)
		g_string_append_printf(out, "poll_seconds=%d\n", s->poll_seconds);

	char *tmp = g_strconcat(path, ".tmp", NULL);
	int fd = open(tmp, O_WRONLY | O_CREAT | O_TRUNC, 0600);
	gboolean ok = fd >= 0;
	if (ok) {
		fchmod(fd, 0600);	// in case the temp file existed with other bits
		ok = write(fd, out->str, out->len) == (ssize_t)out->len && fsync(fd) == 0;
		ok = close(fd) == 0 && ok;
		ok = ok && rename(tmp, path) == 0;
		if (!ok)
			unlink(tmp);
	}
	g_free(tmp);
	g_string_free(out, TRUE);
	return ok;
}

/**
 * A JSON string literal. Control characters are escaped and bytes that are not valid UTF-8 become '?', since the
 * server's JSON parser rejects invalid UTF-8 (a log tail can start in the middle of a character).
 */
char *
json_string(const char *text, gsize length)
{
	GString *out = g_string_sized_new(length + 2);
	const unsigned char *s = (const unsigned char *)text;
	g_string_append_c(out, '"');
	for (gsize i = 0; i < length; i++) {
		unsigned char c = s[i];
		if (c == '"' || c == '\\') {
			g_string_append_c(out, '\\');
			g_string_append_c(out, c);
		} else if (c < 0x20) {
			g_string_append_printf(out, "\\u%04x", c);
		} else if (c < 0x80) {
			g_string_append_c(out, c);
		} else {
			// shortcut: overlong 3/4-byte forms and surrogates are not rejected; add if the server ever refuses a log.
			gsize n = c >= 0xc2 && c < 0xe0 ? 2 : c >= 0xe0 && c < 0xf0 ? 3 : c >= 0xf0 && c < 0xf5 ? 4 : 0;
			gboolean valid = n > 0 && i + n <= length;
			for (gsize k = 1; valid && k < n; k++)
				valid = (s[i + k] & 0xc0) == 0x80;
			if (valid) {
				g_string_append_len(out, text + i, n);
				i += n - 1;
			} else
				g_string_append_c(out, '?');
		}
	}
	g_string_append_c(out, '"');
	return g_string_free(out, FALSE);
}

/** The last 5 MB of the build log (the end is where the error is). */
GString *
read_log_tail(const char *path)
{
	const off_t max = 5 << 20;
	GString *text = g_string_new(NULL);
	int fd = open(path, O_RDONLY);
	if (fd < 0)
		return text;
	off_t size = lseek(fd, 0, SEEK_END);
	off_t start = size > max ? size - max : 0;
	g_string_set_size(text, size - start);
	ssize_t n = pread(fd, text->str, size - start, start);
	g_string_set_size(text, n > 0 ? n : 0);
	close(fd);
	return text;
}
