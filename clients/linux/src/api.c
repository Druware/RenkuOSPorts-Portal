/*
 * HTTP client for the coordinator's build client API (libsoup 3, synchronous calls on the worker thread), with the
 * same token handling as the Haiku and .NET clients: a token is fetched on first use and refreshed before its 12 h
 * lifetime ends; a 401 gets a new token and the request is retried once.
 * MIT License.
 */
#include "builder.h"

#include <json-glib/json-glib.h>
#include <string.h>

//! Tokens live 12 h on the server; get a new one an hour before (a 401 also refreshes it).
static const gint64 kTokenRefresh = G_GINT64_CONSTANT(11) * 3600 * G_USEC_PER_SEC;

void
api_init(Api *api, const char *server, const char *client_id, const char *secret, GCancellable *cancel)
{
	memset(api, 0, sizeof(*api));
	char *trimmed = g_strstrip(g_strdup(server));
	api->server = g_str_has_suffix(trimmed, "/") ? g_strdup(trimmed) : g_strconcat(trimmed, "/", NULL);
	g_free(trimmed);
	api->client_id = g_strstrip(g_strdup(client_id));
	api->secret = g_strstrip(g_strdup(secret));
	// timeout: inactivity on the socket, so a large upload is not cut off while it is moving.
	api->session = soup_session_new_with_options("user-agent", "renkuports-builder/" APP_VERSION,
		"timeout", 300, NULL);
	api->cancel = cancel ? g_object_ref(cancel) : g_cancellable_new();
}

void
api_clear(Api *api)
{
	g_clear_object(&api->session);
	g_clear_object(&api->cancel);
	g_clear_pointer(&api->server, g_free);
	g_clear_pointer(&api->client_id, g_free);
	g_clear_pointer(&api->secret, g_free);
	g_clear_pointer(&api->token, g_free);
}

/** One HTTP exchange; TRUE when a response (any status) arrived. Takes ownership of @p body. */
static gboolean
http_send(Api *api, const char *method, const char *url, const char *token, GInputStream *body, goffset size,
	const char *type, guint *status, GBytes **reply, char **error)
{
	*status = 0;
	SoupMessage *msg = soup_message_new(method, url);
	if (msg == NULL) {
		*error = g_strdup_printf("invalid URL %s", url);
		g_clear_object(&body);
		return FALSE;
	}
	// A redirect would turn a POST into a GET without its body; report it instead (e.g. http:// for an https site).
	soup_message_add_flags(msg, SOUP_MESSAGE_NO_REDIRECT);
	if (token) {
		char *auth = g_strconcat("Bearer ", token, NULL);
		soup_message_headers_replace(soup_message_get_request_headers(msg), "Authorization", auth);
		g_free(auth);
	}
	if (body == NULL && !strcmp(method, "POST"))
		body = g_memory_input_stream_new();	// "Content-Length: 0" rather than no length at all
	if (body) {
		soup_message_set_request_body(msg, type ? type : "text/plain", body, size);
		g_object_unref(body);
	}
	GError *err = NULL;
	GBytes *bytes = soup_session_send_and_read(api->session, msg, api->cancel, &err);
	*status = soup_message_get_status(msg);
	g_object_unref(msg);
	if (bytes == NULL) {
		*error = g_strdup(err->message);
		g_error_free(err);
		*status = 0;
		return FALSE;
	}
	if (reply)
		*reply = bytes;
	else
		g_bytes_unref(bytes);
	return TRUE;
}

/** The root object of a JSON reply, or NULL. Free with g_object_unref on the returned parser. */
static JsonObject *
parse(GBytes *bytes, JsonParser **parser)
{
	*parser = json_parser_new();
	gsize n;
	const char *data = g_bytes_get_data(bytes, &n);
	if (!json_parser_load_from_data(*parser, data, n, NULL))
		return NULL;
	JsonNode *root = json_parser_get_root(*parser);
	return root && JSON_NODE_HOLDS_OBJECT(root) ? json_node_get_object(root) : NULL;
}

static gboolean
token(Api *api, char **error)
{
	if (api->token && g_get_monotonic_time() - api->token_at < kTokenRefresh)
		return TRUE;
	char *id = json_string(api->client_id, strlen(api->client_id)), *secret = json_string(api->secret, strlen(api->secret));
	char *json = g_strdup_printf("{\"clientId\":%s,\"secret\":%s}", id, secret);
	g_free(id);
	g_free(secret);
	gsize n = strlen(json);
	GInputStream *body = g_memory_input_stream_new_from_data(json, n, g_free);
	char *url = g_strconcat(api->server, "api/client/token", NULL), *err = NULL;
	guint status;
	GBytes *reply = NULL;
	gboolean ok = http_send(api, "POST", url, NULL, body, n, "application/json", &status, &reply, &err);
	g_free(url);
	if (!ok) {
		*error = g_strdup_printf("cannot reach %s: %s", api->server, err);
		g_free(err);
		return FALSE;
	}
	JsonParser *parser;
	JsonObject *o = parse(reply, &parser);
	const char *t = o && status == 200 ? json_object_get_string_member_with_default(o, "accessToken", NULL) : NULL;
	if (t) {
		g_free(api->token);
		api->token = g_strdup(t);
		api->token_at = g_get_monotonic_time();
	} else
		{
		gsize n;
		const char *body = g_bytes_get_data(reply, &n);
		*error = g_strdup_printf("token request failed: %u %.*s", status, (int)MIN(n, 300), body ? body : "");
	}
	g_object_unref(parser);
	g_bytes_unref(reply);
	return t != NULL;
}

gboolean
api_call(Api *api, const char *method, const char *path, BodyFunc make_body, gpointer data, guint *status,
	GBytes **reply, char **error)
{
	*status = 0;
	if (reply)
		*reply = NULL;
	for (int attempt = 0;; attempt++) {
		if (!token(api, error))
			return FALSE;
		goffset size = 0;
		char *type = NULL;
		GInputStream *body = NULL;
		if (make_body && (body = make_body(data, &size, &type, error)) == NULL)
			return FALSE;
		char *url = g_strconcat(api->server, path, NULL), *err = NULL;
		GBytes *bytes = NULL;
		gboolean ok = http_send(api, method, url, api->token, body, size, type, status, &bytes, &err);
		g_free(url);
		g_free(type);
		if (!ok) {
			*error = g_strdup_printf("%s %s: %s", method, path, err);
			g_free(err);
			return FALSE;
		}
		if (*status == 401 && attempt == 0) {
			g_clear_pointer(&api->token, g_free);
			g_bytes_unref(bytes);
			continue;
		}
		if (*status >= 200 && *status < 300) {
			if (reply)
				*reply = bytes;
			else
				g_bytes_unref(bytes);
			return TRUE;
		}
		gsize n;
		const char *text = g_bytes_get_data(bytes, &n);
		*error = g_strdup_printf("%s %s: %u %.*s", method, path, *status, (int)MIN(n, 500), text ? text : "");
		g_bytes_unref(bytes);
		return FALSE;
	}
}

static GInputStream *
json_body(gpointer data, goffset *size, char **type, char **error)
{
	(void)error;
	*size = strlen(data);
	*type = g_strdup("application/json");
	return g_memory_input_stream_new_from_data(data, *size, NULL);	// data outlives the call
}

gboolean
api_post_json(Api *api, const char *path, const char *json, guint *status, GBytes **reply, char **error)
{
	return api_call(api, "POST", path, json_body, (gpointer)json, status, reply, error);
}

gboolean
api_summary(Api *api, char **text, char **platform, char **error)
{
	guint status;
	GBytes *reply;
	if (!api_call(api, "GET", "api/client/me", NULL, NULL, &status, &reply, error))
		return FALSE;
	JsonParser *parser;
	JsonObject *me = parse(reply, &parser);
	g_bytes_unref(reply);
	if (me == NULL) {
		g_object_unref(parser);
		*error = g_strdup("api/client/me: unreadable reply");
		return FALSE;
	}
	const char *plat = json_object_get_string_member_with_default(me, "platform", "?");
	GString *out = g_string_new(NULL);
	g_string_printf(out, "%s (%s, owner %s)", json_object_get_string_member_with_default(me, "name", "?"), plat,
		json_object_get_string_member_with_default(me, "owner", "?"));
	if (platform)
		*platform = g_strdup(plat);

	char *path = g_strconcat("api/client/jobs?status=Queued&pageSize=1&platform=", plat, NULL), *err = NULL;
	if (api_call(api, "GET", path, NULL, NULL, &status, &reply, &err)) {
		JsonParser *p2;
		JsonObject *page = parse(reply, &p2);
		if (page)
			g_string_append_printf(out, ": %" G_GINT64_FORMAT " job(s) queued for %s",
				(gint64)json_object_get_double_member_with_default(page, "total", 0), plat);
		g_object_unref(p2);
		g_bytes_unref(reply);
	}
	g_free(err);
	g_free(path);

	JsonNode *node = json_object_get_member(me, "currentJob");
	if (node && JSON_NODE_HOLDS_OBJECT(node)) {
		JsonObject *job = json_node_get_object(node);
		g_string_append_printf(out, "; building %s-%s (job %" G_GINT64_FORMAT ", attempt %d",
			json_object_get_string_member_with_default(job, "portName", "?"),
			json_object_get_string_member_with_default(job, "version", "?"),
			(gint64)json_object_get_double_member_with_default(job, "id", 0),
			(int)json_object_get_double_member_with_default(job, "attempts", 0));
		if (json_object_has_member(job, "unblocks"))
			g_string_append_printf(out, ", unblocks %d queued", (int)json_object_get_double_member(job, "unblocks"));
		g_string_append(out, ")");
	} else
		g_string_append(out, "; no job claimed");
	g_object_unref(parser);
	*text = g_string_free(out, FALSE);
	return TRUE;
}

static gboolean
post_report(Api *api, gint64 id, const char *what, const char *field, const char *text, const GString *log,
	guint *status, char **error)
{
	char *t = json_string(text, strlen(text)), *l = json_string(log->str, log->len);
	char *json = g_strdup_printf("{\"%s\":%s,\"log\":%s}", field, t, l);
	char *path = g_strdup_printf("api/client/jobs/%" G_GINT64_FORMAT "/%s", id, what);
	gboolean ok = api_post_json(api, path, json, status, NULL, error);
	g_free(t); g_free(l); g_free(json); g_free(path);
	return ok;
}

gboolean
api_fail(Api *api, gint64 id, const char *summary, const GString *log, char **error)
{
	guint status;
	return post_report(api, id, "fail", "summary", summary, log, &status, error);
}

gboolean
api_release(Api *api, gint64 id, const char *reason, const GString *log, char **error)
{
	guint status;
	if (post_report(api, id, "release", "reason", reason, log, &status, error) || status != 404)
		return status >= 200 && status < 300;
	// An older server without the release endpoint: the job can only be failed.
	g_clear_pointer(error, g_free);
	char *summary = g_strconcat("infrastructure: ", reason, NULL);
	gboolean ok = api_fail(api, id, summary, log, error);
	g_free(summary);
	return ok;
}
