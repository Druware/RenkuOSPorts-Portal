/*
 * Unit tests of the UI-free core: settings, JSON escaping, multipart layout, exit code mapping, the docker argv,
 * and the API's 401 retry and release fallback against a SoupServer stub.
 * MIT License.
 */
#include "builder.h"

#include <glib/gstdio.h>
#include <string.h>
#include <sys/stat.h>

static char *
temp_dir(void)
{
	return g_dir_make_tmp("renkuports-test-XXXXXX", NULL);
}

static void
test_settings_round_trip(void)
{
	char *dir = temp_dir(), *path = g_build_filename(dir, "sub", "settings", NULL);
	Settings s, t;
	settings_init(&s);
	g_assert_cmpstr(s.docker_path, ==, "docker");
	g_assert_cmpstr(s.kvm, ==, "auto");
	g_assert_cmpint(s.guest_mb, ==, 8192);
	g_free(s.secret);
	s.secret = g_strdup("se\ncret");	// a pasted newline must not split the line
	g_free(s.cache_volume);
	s.cache_volume = g_strdup("renkuports-cache-b");
	s.jobs = 8;
	s.run_on_start = TRUE;
	g_assert_true(settings_save(&s, path));

	GStatBuf st;
	g_assert_cmpint(g_stat(path, &st), ==, 0);
	g_assert_cmpint(st.st_mode & 0777, ==, 0600);

	settings_init(&t);
	g_assert_true(settings_load(&t, path));
	g_assert_cmpstr(t.secret, ==, "secret");
	g_assert_cmpstr(t.cache_volume, ==, "renkuports-cache-b");
	g_assert_cmpint(t.jobs, ==, 8);
	g_assert_true(t.run_on_start);
	g_assert_cmpint(t.poll_seconds, ==, 60);

	// poll_seconds has a minimum of 5; build_command is read though there is no field for it.
	g_assert_true(g_file_set_contents(path, "poll_seconds=1\nbuild_command=/bin/true\n# x=y\n", -1, NULL));
	g_assert_true(settings_load(&t, path));
	g_assert_cmpint(t.poll_seconds, ==, 5);
	g_assert_cmpstr(t.build_command, ==, "/bin/true");
	g_assert_false(settings_load(&t, "/nonexistent/settings"));
	settings_clear(&s);
	settings_clear(&t);
	g_free(path);
	g_free(dir);
}

static void
test_json_string(void)
{
	struct { const char *in; gssize len; const char *out; } cases[] = {
		{"plain", -1, "\"plain\""},
		{"a\"b\\c", -1, "\"a\\\"b\\\\c\""},
		{"line\n\ttab\x01", -1, "\"line\\u000a\\u0009tab\\u0001\""},
		{"caf\xc3\xa9 \xe2\x82\xac", -1, "\"caf\xc3\xa9 \xe2\x82\xac\""},
		{"bad \xff byte", -1, "\"bad ? byte\""},
		{"\xa9tail", -1, "\"?tail\""},	// a tail starting mid-character
		{"cut \xe2\x82", -1, "\"cut ??\""},	// truncated at the end
	};
	for (gsize i = 0; i < G_N_ELEMENTS(cases); i++) {
		char *s = json_string(cases[i].in, strlen(cases[i].in));
		g_assert_cmpstr(s, ==, cases[i].out);
		g_free(s);
	}
	char nul[] = {'a', 0, 'b'};
	char *s = json_string(nul, 3);
	g_assert_cmpstr(s, ==, "\"a\\u0000b\"");
	g_free(s);
}

static void
test_log_tail(void)
{
	char *dir = temp_dir(), *path = g_build_filename(dir, "build.log", NULL);
	gsize size = (5 << 20) + 10;
	char *data = g_malloc(size);
	memset(data, 'a', 10);
	memset(data + 10, 'b', size - 10);
	g_assert_true(g_file_set_contents(path, data, size, NULL));
	GString *tail = read_log_tail(path);
	g_assert_cmpuint(tail->len, ==, 5 << 20);
	g_assert_null(memchr(tail->str, 'a', tail->len));
	g_string_free(tail, TRUE);
	g_free(data); g_free(path); g_free(dir);
}

static void
test_multipart(void)
{
	char *dir = temp_dir(), *path = g_build_filename(dir, "foo-1.0-1-arm64.hpkg", NULL);
	g_assert_true(g_file_set_contents(path, "PKG\0DATA", 8, NULL));
	char *head = multipart_head("B", "abc", "2026-11-18T10:00:00Z", "foo-1.0-1-arm64.hpkg"), *tail = multipart_tail("B");
	GInputStream *s = multipart_stream(head, path, tail, NULL);
	g_assert_nonnull(s);
	char buffer[1024];
	gsize n = 0;
	g_assert_true(g_input_stream_read_all(s, buffer, sizeof(buffer), &n, NULL, NULL));
	const char expected[] = "--B\r\nContent-Disposition: form-data; name=\"sha256\"\r\n\r\nabc\r\n"
		"--B\r\nContent-Disposition: form-data; name=\"buildDate\"\r\n\r\n2026-11-18T10:00:00Z\r\n"
		"--B\r\nContent-Disposition: form-data; name=\"file\"; filename=\"foo-1.0-1-arm64.hpkg\"\r\n"
		"Content-Type: application/octet-stream\r\n\r\nPKG\0DATA\r\n--B--\r\n";
	g_assert_cmpmem(buffer, n, expected, sizeof(expected) - 1);
	g_assert_cmpuint(n, ==, strlen(head) + 8 + strlen(tail));
	g_object_unref(s);
	g_assert_null(multipart_stream(head, "/nonexistent.hpkg", tail, NULL));
	g_free(head); g_free(tail); g_free(path); g_free(dir);
}

static void
test_exit_action(void)
{
	struct { int code; gboolean stopped; JobAction action; const char *summary; } cases[] = {
		{0, FALSE, ACTION_COMPLETE, NULL},
		{1, FALSE, ACTION_FAIL, "build failed (exit 1)"},
		{2, FALSE, ACTION_FAIL_PAUSE, "the container rejected its arguments (exit 2); client paused"},
		{3, FALSE, ACTION_RELEASE, "build environment failed (exit 3)"},
		{4, FALSE, ACTION_RETRY, "cache volume busy (exit 4)"},
		{5, FALSE, ACTION_RELEASE, "guest image missing or invalid (exit 5); run Prepare"},
		{75, FALSE, ACTION_RELEASE, "client stopped"},
		{125, FALSE, ACTION_RELEASE, "docker failed or the container was killed (exit 125)"},
		{126, FALSE, ACTION_RELEASE, NULL},
		{127, FALSE, ACTION_RELEASE, NULL},
		{137, FALSE, ACTION_RELEASE, NULL},
		{137, TRUE, ACTION_RELEASE, "client stopped"},
		{0, TRUE, ACTION_RELEASE, "client stopped"},
		{42, FALSE, ACTION_FAIL, "build failed (exit 42)"},
	};
	for (gsize i = 0; i < G_N_ELEMENTS(cases); i++) {
		char *summary = NULL;
		g_assert_cmpint(exit_action(cases[i].code, cases[i].stopped, &summary), ==, cases[i].action);
		if (cases[i].summary || cases[i].code == 0)
			g_assert_cmpstr(summary, ==, cases[i].summary);
		g_free(summary);
	}
}

static char *
argv_line(const char *kvm, gboolean present)
{
	Settings s;
	settings_init(&s);
	g_free(s.kvm);
	s.kvm = g_strdup(kvm);
	g_free(s.image);
	s.image = g_strdup("renkuports-builder:dev");
	Job job = {42, "libfoo", "1.2", "arm64", "dev-libs/libfoo/libfoo-1.2.recipe", "https://example.org/ports.git", "main"};
	GPtrArray *a = docker_run_argv(&s, &job, "/work/job-42/out", present);
	char *line = g_strjoinv(" ", (char **)a->pdata);
	g_ptr_array_free(a, TRUE);
	settings_clear(&s);
	return line;
}

static void
test_docker_argv(void)
{
	const char *env = "-v renkuports-cache:/cache -v /work/job-42/out:/out -e RENKUPORTS_JOB_ID=42 -e RENKUPORTS_PLATFORM=arm64 "
		"-e RENKUPORTS_RECIPE_PATH=dev-libs/libfoo/libfoo-1.2.recipe -e RENKUPORTS_PORTS_URL=https://example.org/ports.git "
		"-e RENKUPORTS_PORTS_REF=main -e RENKUPORTS_JOBS=4 -e RENKUPORTS_GUEST_MB=8192";
	const char *tail = "renkuports-builder:dev build arm64 libfoo 1.2";
	struct { const char *kvm; gboolean present; const char *device, *accel; } cases[] = {
		{"auto", TRUE, " --device /dev/kvm", "auto"},
		{"auto", FALSE, "", "auto"},
		{"on", FALSE, " --device /dev/kvm", "kvm"},
		{"off", TRUE, "", "tcg"},
	};
	for (gsize i = 0; i < G_N_ELEMENTS(cases); i++) {
		char *line = argv_line(cases[i].kvm, cases[i].present);
		char *expected = g_strdup_printf("run --rm --init --name renkuports-job-42%s %s -e RENKUPORTS_ACCEL=%s %s",
			cases[i].device, env, cases[i].accel, tail);
		g_assert_cmpstr(line, ==, expected);
		g_free(line);
		g_free(expected);
	}
}

/* ---- API against a SoupServer stub on its own thread ---- */

typedef struct {
	GMainContext *ctx;
	GMainLoop *loop;
	SoupServer *server;
	char *url;
	gboolean has_release;
	int tokens, me_401;	/**< token requests; how many 401s /me still returns */
	GString *calls;	/**< "METHOD path body" lines */
	GMutex lock;
	GCond ready;
} Stub;

static void
handler(SoupServer *server, SoupServerMessage *msg, const char *path, GHashTable *query, gpointer data)
{
	(void)server; (void)query;
	Stub *stub = data;
	SoupMessageBody *body = soup_server_message_get_request_body(msg);
	GBytes *bytes = soup_message_body_flatten(body);
	g_string_append_printf(stub->calls, "%s %s %.*s\n", soup_server_message_get_method(msg), path,
		(int)g_bytes_get_size(bytes), (const char *)g_bytes_get_data(bytes, NULL));
	g_bytes_unref(bytes);
	const char *json = NULL;
	guint status = 200;
	if (!strcmp(path, "/api/client/token")) {
		json = stub->tokens++ == 0 ? "{\"accessToken\":\"t1\"}" : "{\"accessToken\":\"t2\"}";
	} else if (!strcmp(path, "/api/client/me")) {
		if (stub->me_401 > 0) {
			stub->me_401--;
			status = 401;
		} else
			json = "{\"name\":\"c\",\"platform\":\"arm64\",\"owner\":\"o\"}";
	} else if (g_str_has_prefix(path, "/api/client/jobs/7/release"))
		status = stub->has_release ? 204 : 404;
	else if (g_str_has_prefix(path, "/api/client/jobs/7/fail"))
		status = 204;
	else if (!strcmp(path, "/api/client/jobs"))
		json = "{\"total\":3}";
	else
		status = 404;
	soup_server_message_set_status(msg, status, NULL);
	if (json)
		soup_server_message_set_response(msg, "application/json", SOUP_MEMORY_COPY, json, strlen(json));
}

static gpointer
stub_thread(gpointer data)
{
	Stub *stub = data;
	g_main_context_push_thread_default(stub->ctx);
	stub->server = soup_server_new(NULL, NULL);
	soup_server_add_handler(stub->server, NULL, handler, stub, NULL);
	g_assert_true(soup_server_listen_local(stub->server, 0, SOUP_SERVER_LISTEN_IPV4_ONLY, NULL));
	GSList *uris = soup_server_get_uris(stub->server);
	g_mutex_lock(&stub->lock);
	stub->url = g_uri_to_string(uris->data);
	g_cond_signal(&stub->ready);
	g_mutex_unlock(&stub->lock);
	g_slist_free_full(uris, (GDestroyNotify)g_uri_unref);
	g_main_loop_run(stub->loop);
	g_object_unref(stub->server);
	g_main_context_pop_thread_default(stub->ctx);
	return NULL;
}

static GThread *
stub_start(Stub *stub)
{
	memset(stub, 0, sizeof(*stub));
	stub->ctx = g_main_context_new();
	stub->loop = g_main_loop_new(stub->ctx, FALSE);
	stub->calls = g_string_new(NULL);
	g_mutex_init(&stub->lock);
	g_cond_init(&stub->ready);
	GThread *t = g_thread_new("stub", stub_thread, stub);
	g_mutex_lock(&stub->lock);
	while (!stub->url)
		g_cond_wait(&stub->ready, &stub->lock);
	g_mutex_unlock(&stub->lock);
	return t;
}

static void
stub_stop(Stub *stub, GThread *t)
{
	g_main_loop_quit(stub->loop);
	g_main_context_wakeup(stub->ctx);
	g_thread_join(t);
	g_main_loop_unref(stub->loop);
	g_main_context_unref(stub->ctx);
	g_string_free(stub->calls, TRUE);
	g_free(stub->url);
}

static void
release_case(gboolean has_release, const char *expected_calls)
{
	Stub stub;
	GThread *t = stub_start(&stub);
	stub.has_release = has_release;
	Api api;
	api_init(&api, stub.url, "id", "secret", NULL);
	GString *log = g_string_new("line\n");
	char *error = NULL;
	g_assert_true(api_release(&api, 7, "client stopped", log, &error));
	g_assert_null(error);
	g_assert_cmpstr(stub.calls->str, ==, expected_calls);
	api_clear(&api);
	g_string_free(log, TRUE);
	stub_stop(&stub, t);
}

static void
test_release(void)
{
	release_case(TRUE, "POST /api/client/token {\"clientId\":\"id\",\"secret\":\"secret\"}\n"
		"POST /api/client/jobs/7/release {\"reason\":\"client stopped\",\"log\":\"line\\u000a\"}\n");
}

static void
test_release_fallback(void)
{
	release_case(FALSE, "POST /api/client/token {\"clientId\":\"id\",\"secret\":\"secret\"}\n"
		"POST /api/client/jobs/7/release {\"reason\":\"client stopped\",\"log\":\"line\\u000a\"}\n"
		"POST /api/client/jobs/7/fail {\"summary\":\"infrastructure: client stopped\",\"log\":\"line\\u000a\"}\n");
}

static void
test_401_retry(void)
{
	Stub stub;
	GThread *t = stub_start(&stub);
	Api api;
	api_init(&api, stub.url, "id", "secret", NULL);
	char *text = NULL, *platform = NULL, *error = NULL;
	stub.me_401 = 1;	// one 401: a new token and one retry
	g_assert_true(api_summary(&api, &text, &platform, &error));
	g_assert_cmpstr(text, ==, "c (arm64, owner o): 3 job(s) queued for arm64; no job claimed");
	g_assert_cmpstr(platform, ==, "arm64");
	g_assert_cmpint(stub.tokens, ==, 2);
	g_assert_cmpstr(api.token, ==, "t2");
	g_free(text); g_free(platform);
	stub.me_401 = 2;	// two 401s: only one retry
	guint status;
	g_assert_false(api_call(&api, "GET", "api/client/me", NULL, NULL, &status, NULL, &error));
	g_assert_cmpuint(status, ==, 401);
	g_assert_nonnull(error);
	g_free(error);
	api_clear(&api);
	stub_stop(&stub, t);
}

int
main(int argc, char **argv)
{
	g_test_init(&argc, &argv, NULL);
	g_test_add_func("/settings/round-trip", test_settings_round_trip);
	g_test_add_func("/text/json-string", test_json_string);
	g_test_add_func("/text/log-tail", test_log_tail);
	g_test_add_func("/upload/multipart", test_multipart);
	g_test_add_func("/container/exit-action", test_exit_action);
	g_test_add_func("/container/docker-argv", test_docker_argv);
	g_test_add_func("/api/release", test_release);
	g_test_add_func("/api/release-fallback", test_release_fallback);
	g_test_add_func("/api/401-retry", test_401_retry);
	return g_test_run();
}
