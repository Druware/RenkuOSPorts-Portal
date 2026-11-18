/*
 * RenkuPorts Builder for Linux: a GTK4 build client for the RenkuOSPorts coordinator that builds each job in the
 * renkuports-builder container (docs/linux-mac-build-clients.md). Shared declarations of the settings, the HTTP
 * client, the container helpers and the build worker. The UI is in main.c; everything here is UI-free.
 * MIT License.
 */
#ifndef RENKUPORTS_BUILDER_H
#define RENKUPORTS_BUILDER_H

#include <gio/gio.h>
#include <libsoup/soup.h>

#ifndef APP_VERSION
#define APP_VERSION "0.1.1"
#endif
/** shortcut: the registry is still an open question (spec G 10); GHCR under the repository owner is assumed. */
#define DEFAULT_IMAGE "ghcr.io/druware/renkuports-builder:" APP_VERSION

/** Settings, stored as key=value lines in settings_path() (mode 0600: it holds the secret). */
typedef struct {
	char *server, *client_id, *secret;
	char *docker_path, *image, *kvm, *cache_volume, *pull_policy, *work_dir;
	char *build_command;	/**< hidden test hook: "<cmd> <port> <version> <outdir>" instead of the container */
	int jobs, guest_mb, poll_seconds;
	gboolean run_on_start;
} Settings;

void settings_init(Settings *s);	/**< the defaults */
void settings_clear(Settings *s);
void settings_copy(Settings *to, const Settings *from);
char *settings_path(void);	/**< ${XDG_CONFIG_HOME:-~/.config}/renkuports-builder/settings */
gboolean settings_load(Settings *s, const char *path);	/**< FALSE if the file does not exist */
gboolean settings_save(const Settings *s, const char *path);

char *json_string(const char *text, gsize length);
GString *read_log_tail(const char *path);

/** The coordinator's client API (synchronous libsoup, for the worker thread). */
typedef struct {
	SoupSession *session;
	char *server, *client_id, *secret, *token;
	gint64 token_at;
	GCancellable *cancel;
} Api;

/** Makes a request body (called again for the 401 retry). Returns NULL with *error set on failure. */
typedef GInputStream *(*BodyFunc)(gpointer data, goffset *size, char **type, char **error);

/** @p cancel (may be NULL) aborts the request in flight when cancelled. */
void api_init(Api *api, const char *server, const char *client_id, const char *secret, GCancellable *cancel);
void api_clear(Api *api);
/** Sends a request with the bearer token; TRUE on 2xx. *status is the HTTP status (0: no response). */
gboolean api_call(Api *api, const char *method, const char *path, BodyFunc make_body, gpointer data,
	guint *status, GBytes **reply, char **error);
gboolean api_post_json(Api *api, const char *path, const char *json, guint *status, GBytes **reply, char **error);
/** One line about this client and the queue; *platform (optional) gets the client's platform. */
gboolean api_summary(Api *api, char **text, char **platform, char **error);
gboolean api_fail(Api *api, gint64 id, const char *summary, const GString *log, char **error);
/** Gives the job back (Queued); on 404 (a server without the endpoint) fails it with "infrastructure: <reason>". */
gboolean api_release(Api *api, gint64 id, const char *reason, const GString *log, char **error);

/** multipart/form-data upload: sha256 and buildDate fields, then the file part. */
char *multipart_head(const char *boundary, const char *sha, const char *date, const char *filename);
char *multipart_tail(const char *boundary);
/** A stream of @p head, the file at @p path, then @p tail; NULL if the file cannot be opened. */
GInputStream *multipart_stream(const char *head, const char *path, const char *tail, GError **error);

/** What the client does with the container's exit code (spec A, with the release endpoint). */
typedef enum { ACTION_COMPLETE, ACTION_FAIL, ACTION_FAIL_PAUSE, ACTION_RELEASE, ACTION_RETRY } JobAction;
JobAction exit_action(int code, gboolean stopped, char **summary);

typedef struct {
	gint64 id;
	char *port, *version, *platform, *recipe, *ports_url, *ports_ref;
} Job;

/** The `docker run` argv of spec A (without the docker binary itself). */
GPtrArray *docker_run_argv(const Settings *s, const Job *job, const char *outdir, gboolean kvm_present);
char *container_name(gint64 job_id);
/** The docker binary: docker_path if it has a slash, else PATH, then the usual install directories. */
char *resolve_docker(const char *docker_path);

typedef enum { EMIT_STATE, EMIT_JOB, EMIT_LOG, EMIT_LOG_CLEAR, EMIT_RESULT, EMIT_QUEUE, EMIT_EXITED } EmitKind;
/** Called on the worker thread; the GUI forwards to the main loop, headless mode prints. */
typedef void (*EmitFunc)(EmitKind kind, const char *text, gpointer data);

typedef struct {
	Settings s;
	Api api;
	GThread *thread;
	gint stop, pause;	/**< atomics */
	GCancellable *cancel;	/**< Stop aborts the request in flight */
	gboolean once;	/**< headless --once: pause after the first job */
	EmitFunc emit;
	gpointer emit_data;
	char *docker;	/**< resolved docker binary */
	char *accel;	/**< from `info --json` for the client's platform */
	char *job_line;	/**< the Status tab's job line */
	gboolean booting;	/**< no guest output yet in this job */
	int log_fd;
	GString *pending;
	gint64 flushed_at;
} Worker;

void worker_init(Worker *w, EmitFunc emit, gpointer data);
void worker_clear(Worker *w);	/**< frees a worker that is not running */
void worker_start(Worker *w, const Settings *s);	/**< starts the loop, or un-pauses it */
void worker_pause(Worker *w);
void worker_stop(Worker *w);	/**< stops the container and gives the job back */
gboolean worker_running(Worker *w);
void worker_join(Worker *w);
/** Pre-flight of spec E for @p platform. Returns FALSE with *error; *report gets a checklist. */
gboolean preflight(Worker *w, const char *platform, gboolean fetch, GString *report, char **error);
/** Runs argv with output merged; into *capture if given, else the log. Returns the exit code. */
int worker_run(Worker *w, GPtrArray *argv, char **env, char **capture, const char *container);

#endif
