/*
 * RenkuPorts Builder for Linux: the window (Status and Settings tabs, as the Haiku client) and a headless mode.
 *   renkuports-builder                 the GTK window
 *   renkuports-builder --headless      no window: polls and builds until SIGINT or SIGTERM (which stop the build)
 *   renkuports-builder --headless --once   one claim attempt (and its job), then exit
 * MIT License.
 */
#include "builder.h"

#include <glib-unix.h>
#include <gtk/gtk.h>
#include <string.h>

//! The log view keeps the last part of the output; the whole log is in <work dir>/job-<id>/build.log.
static const int kMaxLogText = 512 * 1024;

/* ---- headless ---- */

static GMainLoop *headless_loop;

typedef struct {
	EmitKind kind;
	char *text;
	gpointer app;
} Msg;

static gboolean
headless_exited(gpointer data)
{
	(void)data;
	g_main_loop_quit(headless_loop);
	return G_SOURCE_REMOVE;
}

/** Client messages already go to stdout (worker log lines); add the state, queue and results. */
static void
headless_emit(EmitKind kind, const char *text, gpointer data)
{
	(void)data;
	if (kind == EMIT_STATE || kind == EMIT_RESULT || kind == EMIT_QUEUE)
		printf("%s: %s\n", kind == EMIT_STATE ? "state" : kind == EMIT_RESULT ? "result" : "queue", text);
	fflush(stdout);
	if (kind == EMIT_EXITED)
		g_idle_add(headless_exited, NULL);
}

static gboolean
on_signal(gpointer data)
{
	worker_stop(data);
	return G_SOURCE_CONTINUE;
}

static int
run_headless(gboolean once)
{
	Settings s;
	settings_init(&s);
	char *path = settings_path();
	if (!settings_load(&s, path)) {
		fprintf(stderr, "no settings at %s\n", path);
		return 2;
	}
	g_free(path);
	static Worker w;
	worker_init(&w, headless_emit, NULL);
	w.once = once;
	headless_loop = g_main_loop_new(NULL, FALSE);
	g_unix_signal_add(SIGINT, on_signal, &w);
	g_unix_signal_add(SIGTERM, on_signal, &w);
	worker_start(&w, &s);
	g_main_loop_run(headless_loop);
	worker_join(&w);
	settings_clear(&s);
	return 0;
}

/* ---- window ---- */

typedef struct {
	GtkApplication *gapp;
	GtkWindow *window;
	GtkNotebook *tabs;
	Settings s;
	Worker worker;
	GtkEntry *server, *client_id, *docker, *image, *volume, *guest_mb, *jobs, *work_dir;
	GtkWidget *secret;
	GtkDropDown *kvm, *pull;
	GtkCheckButton *run_on_start;
	GtkLabel *settings_status, *state, *job, *elapsed, *queue, *leftover_text;
	GtkWidget *leftover, *start, *pause, *stop;
	GtkTextView *log;
	GtkListBox *results;
	char *leftovers;	/**< containers left running by an earlier session, one per line */
	gint64 job_started;
	gboolean paused, quitting;
} App;

static const char *kKvm[] = {"auto", "on", "off", NULL}, *kPull[] = {"if-missing", "always", NULL};

static GtkEntry *
field(GtkGrid *grid, int row, const char *label, const char *value)
{
	GtkWidget *l = gtk_label_new(label), *e = gtk_entry_new();
	gtk_label_set_xalign(GTK_LABEL(l), 1);
	gtk_editable_set_text(GTK_EDITABLE(e), value);
	gtk_widget_set_hexpand(e, TRUE);
	gtk_grid_attach(grid, l, 0, row, 1, 1);
	gtk_grid_attach(grid, e, 1, row, 1, 1);
	return GTK_ENTRY(e);
}

static GtkDropDown *
choice(GtkGrid *grid, int row, const char *label, const char **values, const char *value)
{
	GtkWidget *l = gtk_label_new(label), *d = gtk_drop_down_new_from_strings(values);
	gtk_label_set_xalign(GTK_LABEL(l), 1);
	for (guint i = 0; values[i]; i++)
		if (!strcmp(values[i], value))
			gtk_drop_down_set_selected(GTK_DROP_DOWN(d), i);
	gtk_widget_set_halign(d, GTK_ALIGN_START);
	gtk_grid_attach(grid, l, 0, row, 1, 1);
	gtk_grid_attach(grid, d, 1, row, 1, 1);
	return GTK_DROP_DOWN(d);
}

static void
set_text(char **to, GtkEditable *from)
{
	g_free(*to);
	*to = g_strdup(gtk_editable_get_text(from));
}

static void
read_fields(App *a)
{
	set_text(&a->s.server, GTK_EDITABLE(a->server));
	set_text(&a->s.client_id, GTK_EDITABLE(a->client_id));
	set_text(&a->s.secret, GTK_EDITABLE(a->secret));
	set_text(&a->s.docker_path, GTK_EDITABLE(a->docker));
	set_text(&a->s.image, GTK_EDITABLE(a->image));
	set_text(&a->s.cache_volume, GTK_EDITABLE(a->volume));
	set_text(&a->s.work_dir, GTK_EDITABLE(a->work_dir));
	g_free(a->s.kvm);
	a->s.kvm = g_strdup(kKvm[gtk_drop_down_get_selected(a->kvm)]);
	g_free(a->s.pull_policy);
	a->s.pull_policy = g_strdup(kPull[gtk_drop_down_get_selected(a->pull)]);
	a->s.jobs = MAX(1, atoi(gtk_editable_get_text(GTK_EDITABLE(a->jobs))));
	a->s.guest_mb = MAX(512, atoi(gtk_editable_get_text(GTK_EDITABLE(a->guest_mb))));
	a->s.run_on_start = gtk_check_button_get_active(a->run_on_start);
}

static void
update_buttons(App *a)
{
	gboolean running = worker_running(&a->worker);
	gtk_widget_set_sensitive(a->start, !a->quitting && (!running || a->paused));
	gtk_widget_set_sensitive(a->pause, running && !a->paused);
	gtk_widget_set_sensitive(a->stop, running);
}

static void
set_label(GtkLabel *label, const char *prefix, const char *text)
{
	char *t = g_strconcat(prefix, text, NULL);
	gtk_label_set_text(label, t);
	g_free(t);
}

/** Worker messages, on the main loop. */
static gboolean
dispatch(gpointer data)
{
	Msg *m = data;
	App *a = m->app;
	switch (m->kind) {
	case EMIT_STATE:
		set_label(a->state, "State: ", m->text);
		break;
	case EMIT_QUEUE:
		set_label(a->queue, "Queue: ", m->text);
		break;
	case EMIT_JOB:
		set_label(a->job, "Job: ", *m->text ? m->text : "none");
		if (!*m->text)
			a->job_started = 0, gtk_label_set_text(a->elapsed, "");
		else if (a->job_started == 0)
			a->job_started = g_get_real_time() / G_USEC_PER_SEC;
		break;
	case EMIT_LOG_CLEAR:
		gtk_text_buffer_set_text(gtk_text_view_get_buffer(a->log), "", 0);
		break;
	case EMIT_LOG: {
		GtkTextBuffer *b = gtk_text_view_get_buffer(a->log);
		GtkTextIter end, start;
		gtk_text_buffer_get_end_iter(b, &end);
		// Invalid UTF-8 from a build would be refused by GTK.
		char *valid = g_utf8_make_valid(m->text, -1);
		gtk_text_buffer_insert(b, &end, valid, -1);
		g_free(valid);
		int count = gtk_text_buffer_get_char_count(b);
		if (count > kMaxLogText) {
			gtk_text_buffer_get_start_iter(b, &start);
			gtk_text_buffer_get_iter_at_offset(b, &end, count - kMaxLogText * 3 / 4);
			gtk_text_buffer_delete(b, &start, &end);
		}
		gtk_text_buffer_get_end_iter(b, &end);
		GtkTextMark *mark = gtk_text_buffer_get_mark(b, "end");
		if (mark)
			gtk_text_buffer_move_mark(b, mark, &end);
		else
			mark = gtk_text_buffer_create_mark(b, "end", &end, FALSE);
		gtk_text_view_scroll_to_mark(a->log, mark, 0, FALSE, 0, 1);
		break;
	}
	case EMIT_RESULT: {
		GDateTime *now = g_date_time_new_now_local();
		char *stamp = g_date_time_format(now, "%Y-%m-%d %H:%M  "), *line = g_strconcat(stamp, m->text, NULL);
		GtkWidget *label = gtk_label_new(line);
		gtk_label_set_xalign(GTK_LABEL(label), 0);
		gtk_label_set_selectable(GTK_LABEL(label), TRUE);
		gtk_list_box_prepend(a->results, label);
		GtkListBoxRow *row;
		while ((row = gtk_list_box_get_row_at_index(a->results, 100)))
			gtk_list_box_remove(a->results, GTK_WIDGET(row));
		g_free(line); g_free(stamp);
		g_date_time_unref(now);
		break;
	}
	case EMIT_EXITED:
		worker_join(&a->worker);
		a->paused = FALSE;
		update_buttons(a);
		if (a->quitting)
			gtk_window_destroy(a->window);
		break;
	}
	g_free(m->text);
	g_free(m);
	return G_SOURCE_REMOVE;
}

/** Called on the worker thread: hand the message to the main loop. */
static void
gui_emit(EmitKind kind, const char *text, gpointer data)
{
	Msg *m = g_new(Msg, 1);
	*m = (Msg){kind, g_strdup(text ? text : ""), data};
	g_idle_add(dispatch, m);
}

static void
on_save(GtkButton *b, App *a)
{
	(void)b;
	read_fields(a);
	char *path = settings_path();
	gtk_label_set_text(a->settings_status, settings_save(&a->s, path)
		? (worker_running(&a->worker) ? "Saved; the running builder uses the new settings after Stop and Start." : "Saved.")
		: "Saving the settings failed.");
	g_free(path);
}

static void
noop_emit(EmitKind kind, const char *text, gpointer data)
{
	(void)kind; (void)text; (void)data;
}

/** Test connection, in its own thread: token, api/client/me and the queue, then the Docker checklist. */
static void
test_thread(GTask *task, gpointer source, gpointer data, GCancellable *cancel)
{
	(void)source; (void)cancel;
	Settings *s = data;
	Api api;
	api_init(&api, s->server, s->client_id, s->secret, NULL);
	char *text = NULL, *platform = NULL, *error = NULL;
	GString *out = g_string_new(NULL);
	if (api_summary(&api, &text, &platform, &error))
		g_string_append_printf(out, "Connected: %s\n", text);
	else
		g_string_append_printf(out, "Connection failed: %s\n", error);
	g_clear_pointer(&error, g_free);
	if (*s->build_command)
		g_string_append(out, "Test build command set: no container, no Docker checks.");
	else if (platform) {
		Worker w;
		worker_init(&w, noop_emit, NULL);
		settings_clear(&w.s);
		settings_copy(&w.s, s);
		if (!preflight(&w, platform, FALSE, out, &error))
			g_string_append_printf(out, "failed: %s", error);
		worker_clear(&w);
	}
	g_free(text); g_free(platform); g_free(error);
	api_clear(&api);
	g_task_return_pointer(task, g_string_free(out, FALSE), g_free);
}

static void
test_done(GObject *source, GAsyncResult *result, gpointer data)
{
	(void)source;
	App *a = data;
	char *text = g_task_propagate_pointer(G_TASK(result), NULL);
	gtk_label_set_text(a->settings_status, text);
	g_free(text);
}

static void
free_settings(gpointer data)
{
	settings_clear(data);
	g_free(data);
}

static void
on_test(GtkButton *b, App *a)
{
	(void)b;
	read_fields(a);
	gtk_label_set_text(a->settings_status, "Testing...");
	Settings *copy = g_new(Settings, 1);
	settings_copy(copy, &a->s);
	GTask *task = g_task_new(NULL, NULL, test_done, a);
	g_task_set_task_data(task, copy, free_settings);
	g_task_run_in_thread(task, test_thread);
	g_object_unref(task);
}

static void
on_start(GtkButton *b, App *a)
{
	(void)b;
	read_fields(a);
	a->paused = FALSE;
	worker_start(&a->worker, &a->s);
	update_buttons(a);
}

static void
on_pause(GtkButton *b, App *a)
{
	(void)b;
	a->paused = TRUE;
	worker_pause(&a->worker);
	gtk_label_set_text(a->state, "State: pausing (finishing the current job)");
	update_buttons(a);
}

static void
on_stop(GtkButton *b, App *a)
{
	(void)b;
	worker_stop(&a->worker);
	gtk_label_set_text(a->state, "State: stopping");
}

/** With a build running, stop it first (the job is given back), then close once the worker has exited. */
static gboolean
on_close(GtkWindow *window, App *a)
{
	(void)window;
	if (!worker_running(&a->worker))
		return FALSE;
	a->quitting = TRUE;
	worker_stop(&a->worker);
	gtk_label_set_text(a->state, "State: stopping before quit");
	update_buttons(a);
	return TRUE;
}

static gboolean
tick(gpointer data)
{
	App *a = data;
	if (a->job_started > 0) {
		gint64 s = g_get_real_time() / G_USEC_PER_SEC - a->job_started;
		char *t = g_strdup_printf("elapsed %" G_GINT64_FORMAT ":%02d:%02d", s / 3600, (int)(s / 60 % 60), (int)(s % 60));
		gtk_label_set_text(a->elapsed, t);
		g_free(t);
	}
	return G_SOURCE_CONTINUE;
}

static void
on_stop_leftovers(GtkButton *b, App *a)
{
	(void)b;
	char **names = g_strsplit(a->leftovers, "\n", -1);
	char *docker = resolve_docker(a->s.docker_path);
	for (char **n = names; *n; n++)
		if (**n) {
			GSubprocess *p = g_subprocess_new(G_SUBPROCESS_FLAGS_STDOUT_SILENCE | G_SUBPROCESS_FLAGS_STDERR_SILENCE,
				NULL, docker, "stop", "-t", "90", *n, NULL);
			g_clear_object(&p);
		}
	g_free(docker);
	g_strfreev(names);
	gtk_widget_set_visible(a->leftover, FALSE);
}

/** A container left running by a crashed client would hold the cache lock: offer to stop it. */
static void
leftovers_listed(GObject *source, GAsyncResult *result, gpointer data)
{
	App *a = data;
	char *out = NULL;
	if (g_subprocess_communicate_utf8_finish(G_SUBPROCESS(source), result, &out, NULL, NULL)
		&& g_subprocess_get_successful(G_SUBPROCESS(source)) && *g_strstrip(out)) {
		g_free(a->leftovers);
		a->leftovers = g_strdup(out);
		char *text = g_strdup_printf("Left running by an earlier session: %s", out);
		gtk_label_set_text(a->leftover_text, text);
		g_free(text);
		gtk_widget_set_visible(a->leftover, TRUE);
	}
	g_free(out);
	g_object_unref(source);
}

static GtkWidget *
button(const char *label, GCallback cb, App *a)
{
	GtkWidget *b = gtk_button_new_with_label(label);
	g_signal_connect(b, "clicked", cb, a);
	return b;
}

static GtkWidget *
left_label(const char *text, GtkLabel **out)
{
	GtkWidget *l = gtk_label_new(text);
	gtk_label_set_xalign(GTK_LABEL(l), 0);
	gtk_label_set_wrap(GTK_LABEL(l), TRUE);
	gtk_label_set_selectable(GTK_LABEL(l), TRUE);
	if (out)
		*out = GTK_LABEL(l);
	return l;
}

static void
activate(GtkApplication *gapp, App *a)
{
	if (a->window) {
		gtk_window_present(a->window);
		return;
	}
	settings_init(&a->s);
	char *path = settings_path();
	gboolean loaded = settings_load(&a->s, path);
	g_free(path);
	worker_init(&a->worker, gui_emit, a);

	GtkGrid *grid = GTK_GRID(gtk_grid_new());
	gtk_grid_set_row_spacing(grid, 6);
	gtk_grid_set_column_spacing(grid, 8);
	a->server = field(grid, 0, "Server URL:", a->s.server);
	a->client_id = field(grid, 1, "Client id:", a->s.client_id);
	GtkWidget *l = gtk_label_new("Secret:");
	gtk_label_set_xalign(GTK_LABEL(l), 1);
	a->secret = gtk_password_entry_new();
	gtk_password_entry_set_show_peek_icon(GTK_PASSWORD_ENTRY(a->secret), TRUE);
	gtk_editable_set_text(GTK_EDITABLE(a->secret), a->s.secret);
	gtk_grid_attach(grid, l, 0, 2, 1, 1);
	gtk_grid_attach(grid, a->secret, 1, 2, 1, 1);
	a->docker = field(grid, 3, "Docker command:", a->s.docker_path);
	a->image = field(grid, 4, "Builder image:", a->s.image);
	a->kvm = choice(grid, 5, "KVM:", kKvm, a->s.kvm);
	a->volume = field(grid, 6, "Cache volume:", a->s.cache_volume);
	char *n = g_strdup_printf("%d", a->s.guest_mb);
	a->guest_mb = field(grid, 7, "Guest memory (MB):", n);
	g_free(n);
	a->pull = choice(grid, 8, "Pull image:", kPull, a->s.pull_policy);
	n = g_strdup_printf("%d", a->s.jobs);
	a->jobs = field(grid, 9, "Jobs (-j):", n);
	g_free(n);
	a->work_dir = field(grid, 10, "Work directory:", a->s.work_dir);
	a->run_on_start = GTK_CHECK_BUTTON(gtk_check_button_new_with_label("Start building when the application starts"));
	gtk_check_button_set_active(a->run_on_start, a->s.run_on_start);
	gtk_grid_attach(grid, GTK_WIDGET(a->run_on_start), 1, 11, 1, 1);

	GtkWidget *settings = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8), *row = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6);
	gtk_box_append(GTK_BOX(row), button("Save", G_CALLBACK(on_save), a));
	gtk_box_append(GTK_BOX(row), button("Test connection", G_CALLBACK(on_test), a));
	gtk_box_append(GTK_BOX(settings), GTK_WIDGET(grid));
	gtk_box_append(GTK_BOX(settings), row);
	gtk_box_append(GTK_BOX(settings), left_label(loaded ? "" : "No settings saved yet.", &a->settings_status));

	GtkWidget *status = gtk_box_new(GTK_ORIENTATION_VERTICAL, 6);
	row = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6);
	gtk_box_append(GTK_BOX(row), a->start = button("Start", G_CALLBACK(on_start), a));
	gtk_box_append(GTK_BOX(row), a->pause = button("Pause", G_CALLBACK(on_pause), a));
	gtk_box_append(GTK_BOX(row), a->stop = button("Stop", G_CALLBACK(on_stop), a));
	gtk_box_append(GTK_BOX(status), row);
	a->leftover = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6);
	gtk_box_append(GTK_BOX(a->leftover), left_label("", &a->leftover_text));
	gtk_box_append(GTK_BOX(a->leftover), button("Stop it", G_CALLBACK(on_stop_leftovers), a));
	gtk_widget_set_visible(a->leftover, FALSE);
	gtk_box_append(GTK_BOX(status), a->leftover);
	gtk_box_append(GTK_BOX(status), left_label("State: not started", &a->state));
	row = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 12);
	gtk_box_append(GTK_BOX(row), left_label("Job: none", &a->job));
	gtk_box_append(GTK_BOX(row), left_label("", &a->elapsed));
	gtk_box_append(GTK_BOX(status), row);
	gtk_box_append(GTK_BOX(status), left_label("Queue: unknown", &a->queue));
	a->log = GTK_TEXT_VIEW(gtk_text_view_new());
	gtk_text_view_set_editable(a->log, FALSE);
	gtk_text_view_set_monospace(a->log, TRUE);
	GtkWidget *scroll = gtk_scrolled_window_new();
	gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroll), GTK_WIDGET(a->log));
	gtk_widget_set_vexpand(scroll, TRUE);
	gtk_box_append(GTK_BOX(status), scroll);
	gtk_box_append(GTK_BOX(status), left_label("Recent results:", NULL));
	a->results = GTK_LIST_BOX(gtk_list_box_new());
	scroll = gtk_scrolled_window_new();
	gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroll), GTK_WIDGET(a->results));
	gtk_widget_set_size_request(scroll, -1, 100);
	gtk_box_append(GTK_BOX(status), scroll);

	a->tabs = GTK_NOTEBOOK(gtk_notebook_new());
	GtkWidget *pages[] = {status, settings};
	for (int i = 0; i < 2; i++) {
		gtk_widget_set_margin_start(pages[i], 12);
		gtk_widget_set_margin_end(pages[i], 12);
		gtk_widget_set_margin_top(pages[i], 12);
		gtk_widget_set_margin_bottom(pages[i], 12);
	}
	gtk_notebook_append_page(a->tabs, status, gtk_label_new("Status"));
	gtk_notebook_append_page(a->tabs, settings, gtk_label_new("Settings"));

	a->window = GTK_WINDOW(gtk_application_window_new(gapp));
	gtk_window_set_title(a->window, "RenkuPorts Builder");
	gtk_window_set_default_size(a->window, 760, 600);
	gtk_window_set_child(a->window, GTK_WIDGET(a->tabs));
	g_signal_connect(a->window, "close-request", G_CALLBACK(on_close), a);
	gtk_window_present(a->window);
	if (!loaded)
		gtk_notebook_set_current_page(a->tabs, 1);
	g_timeout_add_seconds(1, tick, a);
	update_buttons(a);

	if (!*a->s.build_command) {
		char *docker = resolve_docker(a->s.docker_path);
		GSubprocess *p = g_subprocess_new(G_SUBPROCESS_FLAGS_STDOUT_PIPE | G_SUBPROCESS_FLAGS_STDERR_SILENCE, NULL,
			docker, "ps", "--filter", "name=renkuports-job-", "--format", "{{.Names}}", NULL);
		if (p)
			g_subprocess_communicate_utf8_async(p, NULL, NULL, leftovers_listed, a);
		g_free(docker);
	}
	if (a->s.run_on_start)
		on_start(NULL, a);
}

int
main(int argc, char **argv)
{
	gboolean headless = FALSE, once = FALSE;
	for (int i = 1; i < argc; i++) {
		headless = headless || !strcmp(argv[i], "--headless");
		once = once || !strcmp(argv[i], "--once");
	}
	if (headless)
		return run_headless(once);
	static App a;
	a.gapp = gtk_application_new("org.renkuos.PortsBuilder", G_APPLICATION_DEFAULT_FLAGS);
	g_signal_connect(a.gapp, "activate", G_CALLBACK(activate), &a);
	int status = g_application_run(G_APPLICATION(a.gapp), argc, argv);
	g_object_unref(a.gapp);
	return status;
}
