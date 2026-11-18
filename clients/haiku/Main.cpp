/*
 * RenkuPorts Builder: the application and its window (Settings and Status tabs). Plain layout, no styling.
 * MIT License.
 */
#include "Builder.h"

#include <stdio.h>
#include <stdlib.h>
#include <time.h>

#include <Application.h>
#include <Button.h>
#include <CheckBox.h>
#include <LayoutBuilder.h>
#include <ListView.h>
#include <MessageRunner.h>
#include <ScrollView.h>
#include <StringView.h>
#include <TabView.h>
#include <TextControl.h>
#include <TextView.h>
#include <Window.h>

enum {
	MSG_SAVE = 'save', MSG_TEST = 'tst_', MSG_START = 'strt', MSG_PAUSE = 'paus', MSG_STOP = 'stop',
	MSG_PULSE = 'puls'
};

//! The log view keeps the last part of the output; the whole log is in <work dir>/job-<id>/build.log.
static const int32 kMaxLogText = 512 * 1024;


class MainWindow : public BWindow {
public:
	MainWindow();
	void MessageReceived(BMessage* message) override;
	bool QuitRequested() override;

private:
	BTextControl* _Field(const char* label, const char* value);
	void _ReadFields();
	void _UpdateButtons();
	static int32 _Test(void* data);

	Settings fSettings;
	Worker fWorker;
	BTextControl *fServer, *fClientId, *fSecret, *fTree, *fHaikuporter, *fConfig, *fJobs, *fWorkDir;
	BCheckBox* fRunOnStart;
	BStringView *fSettingsStatus, *fState, *fJob, *fElapsed, *fQueue;
	BTextView* fLog;
	BListView* fResults;
	BButton *fStart, *fPause, *fStop;
	BMessageRunner* fPulse;
	int64 fJobStarted = 0;
	bool fPaused = false, fQuitting = false;
};


MainWindow::MainWindow()
	:
	BWindow(BRect(100, 100, 820, 640), "RenkuPorts Builder", B_TITLED_WINDOW,
		B_ASYNCHRONOUS_CONTROLS | B_AUTO_UPDATE_SIZE_LIMITS | B_QUIT_ON_WINDOW_CLOSE),
	fWorker(BMessenger(this))
{
	bool loaded = fSettings.Load() == B_OK;

	fServer = _Field("Server URL:", fSettings.server);
	fClientId = _Field("Client id:", fSettings.clientId);
	fSecret = _Field("Secret:", fSettings.secret);
	fSecret->TextView()->HideTyping(true);	// clears the text, so set it again
	fSecret->SetText(fSettings.secret);
	fTree = _Field("Ports tree:", fSettings.treePath);
	fHaikuporter = _Field("HaikuPorter command:", fSettings.haikuporter);
	fConfig = _Field("HaikuPorter config:", fSettings.haikuporterConfig);
	fJobs = _Field("Jobs (-j):", BString() << fSettings.jobs);
	fWorkDir = _Field("Work directory:", fSettings.workDir);
	fRunOnStart = new BCheckBox("Start building when the application starts");
	fRunOnStart->SetValue(fSettings.runOnStart);
	fSettingsStatus = new BStringView("settings status", loaded ? "" : "No settings saved yet.");

	BView* settings = new BView("Settings", 0);
	BLayoutBuilder::Group<>(settings, B_VERTICAL)
		.SetInsets(B_USE_WINDOW_SPACING)
		.AddGrid()
			.AddTextControl(fServer, 0, 0)
			.AddTextControl(fClientId, 0, 1)
			.AddTextControl(fSecret, 0, 2)
			.AddTextControl(fTree, 0, 3)
			.AddTextControl(fHaikuporter, 0, 4)
			.AddTextControl(fConfig, 0, 5)
			.AddTextControl(fJobs, 0, 6)
			.AddTextControl(fWorkDir, 0, 7)
			.Add(fRunOnStart, 1, 8)
		.End()
		.AddGroup(B_HORIZONTAL)
			.Add(new BButton("Save", new BMessage(MSG_SAVE)))
			.Add(new BButton("Test connection", new BMessage(MSG_TEST)))
			.AddGlue()
		.End()
		.Add(fSettingsStatus)
		.AddGlue();

	fState = new BStringView("state", "State: not started");
	fJob = new BStringView("job", "Job: none");
	fElapsed = new BStringView("elapsed", "");
	fQueue = new BStringView("queue", "Queue: unknown");
	fLog = new BTextView("log");
	fLog->MakeEditable(false);
	fLog->SetStylable(false);
	fLog->SetFontAndColor(be_fixed_font);
	fResults = new BListView("results");
	fStart = new BButton("Start", new BMessage(MSG_START));
	fPause = new BButton("Pause", new BMessage(MSG_PAUSE));
	fStop = new BButton("Stop", new BMessage(MSG_STOP));
	BScrollView* logScroll = new BScrollView("log scroll", fLog, 0, true, true);
	BScrollView* resultScroll = new BScrollView("results scroll", fResults, 0, false, true);
	resultScroll->SetExplicitMinSize(BSize(B_SIZE_UNSET, 90));
	resultScroll->SetExplicitMaxSize(BSize(B_SIZE_UNLIMITED, 120));

	BView* status = new BView("Status", 0);
	BLayoutBuilder::Group<>(status, B_VERTICAL)
		.SetInsets(B_USE_WINDOW_SPACING)
		.AddGroup(B_HORIZONTAL)
			.Add(fStart)
			.Add(fPause)
			.Add(fStop)
			.AddGlue()
		.End()
		.Add(fState)
		.AddGroup(B_HORIZONTAL)
			.Add(fJob)
			.Add(fElapsed)
			.AddGlue()
		.End()
		.Add(fQueue)
		.Add(logScroll, 3)
		.Add(new BStringView("results label", "Recent results:"))
		.Add(resultScroll, 1);

	BTabView* tabs = new BTabView("tabs", B_WIDTH_FROM_LABEL);
	tabs->AddTab(status);
	tabs->AddTab(settings);
	BLayoutBuilder::Group<>(this, B_VERTICAL, 0).Add(tabs);
	if (!loaded)
		tabs->Select(1);

	fPulse = new BMessageRunner(BMessenger(this), new BMessage(MSG_PULSE), 1000000);
	_UpdateButtons();
	if (fSettings.runOnStart)
		PostMessage(MSG_START);
}


BTextControl*
MainWindow::_Field(const char* label, const char* value)
{
	return new BTextControl(label, label, value, NULL);
}


void
MainWindow::_ReadFields()
{
	fSettings.server = fServer->Text();
	fSettings.clientId = fClientId->Text();
	fSettings.secret = fSecret->Text();
	fSettings.treePath = fTree->Text();
	fSettings.haikuporter = fHaikuporter->Text();
	fSettings.haikuporterConfig = fConfig->Text();
	fSettings.jobs = std::max(1, atoi(fJobs->Text()));
	fSettings.workDir = fWorkDir->Text();
	fSettings.runOnStart = fRunOnStart->Value() == B_CONTROL_ON;
}


void
MainWindow::_UpdateButtons()
{
	bool running = fWorker.IsRunning();
	fStart->SetEnabled(!fQuitting && (!running || fPaused));
	fPause->SetEnabled(running && !fPaused);
	fStop->SetEnabled(running);
}


struct TestRequest {
	BMessenger target;
	Settings settings;
};


/** Test connection, in its own thread: get a token and read api/client/me. */
int32
MainWindow::_Test(void* data)
{
	TestRequest* test = (TestRequest*)data;
	ApiClient api;
	api.Configure(test->settings.server, test->settings.clientId, test->settings.secret);
	BString text, error;
	BMessage reply(MSG_TEST_DONE);
	reply.AddString("text", api.Summary(text, error) == B_OK ? BString("Connected: ") << text
		: BString("Connection failed: ") << error);
	test->target.SendMessage(&reply);
	delete test;
	return 0;
}


void
MainWindow::MessageReceived(BMessage* message)
{
	switch (message->what) {
		case MSG_SAVE:
			_ReadFields();
			fSettingsStatus->SetText(fSettings.Save() == B_OK
				? (fWorker.IsRunning() ? "Saved; the running builder uses the new settings after Stop and Start."
					: "Saved.")
				: "Saving the settings failed.");
			break;
		case MSG_TEST:
		{
			_ReadFields();
			fSettingsStatus->SetText("Testing...");
			thread_id thread = spawn_thread(_Test, "connection test", B_NORMAL_PRIORITY,
				new TestRequest{BMessenger(this), fSettings});
			resume_thread(thread);
			break;
		}
		case MSG_TEST_DONE:
			fSettingsStatus->SetText(message->GetString("text", ""));
			break;
		case MSG_START:
			_ReadFields();
			fPaused = false;
			fWorker.Start(fSettings);
			_UpdateButtons();
			break;
		case MSG_PAUSE:
			fPaused = true;
			fWorker.Pause();
			fState->SetText("State: pausing (finishing the current job)");
			_UpdateButtons();
			break;
		case MSG_STOP:
			fWorker.Stop();
			fState->SetText("State: stopping");
			break;
		case MSG_WORKER_EXITED:
			fWorker.WaitForExit();
			fPaused = false;
			_UpdateButtons();
			if (fQuitting) {
				be_app->PostMessage(B_QUIT_REQUESTED);
				Quit();
			}
			break;
		case MSG_STATE:
			fState->SetText(BString("State: ") << message->GetString("state", ""));
			break;
		case MSG_QUEUE:
			fQueue->SetText(BString("Queue: ") << message->GetString("text", ""));
			break;
		case MSG_JOB:
		{
			BString job = message->GetString("job", "");
			fJob->SetText(BString("Job: ") << (job.IsEmpty() ? "none" : job.String()));
			fJobStarted = job.IsEmpty() ? 0 : message->GetInt64("started", 0);
			fElapsed->SetText("");
			break;
		}
		case MSG_PULSE:
			if (fJobStarted > 0) {
				int64 s = real_time_clock() - fJobStarted;
				BString elapsed;
				elapsed.SetToFormat("elapsed %" B_PRId64 ":%02" B_PRId64 ":%02" B_PRId64, s / 3600,
					s / 60 % 60, s % 60);
				fElapsed->SetText(elapsed);
			}
			break;
		case MSG_LOG:
		{
			if (message->GetBool("clear", false))
				fLog->SetText("");
			const char* text;
			ssize_t size;
			if (message->FindData("text", B_STRING_TYPE, (const void**)&text, &size) == B_OK && size > 1) {
				fLog->Insert(fLog->TextLength(), text, size - 1);
				if (fLog->TextLength() > kMaxLogText)
					fLog->Delete(0, fLog->TextLength() - kMaxLogText * 3 / 4);
				fLog->ScrollToOffset(fLog->TextLength());
			}
			break;
		}
		case MSG_RESULT:
		{
			char stamp[32];
			time_t now = time(NULL);
			struct tm tm;
			strftime(stamp, sizeof(stamp), "%Y-%m-%d %H:%M  ", localtime_r(&now, &tm));
			fResults->AddItem(new BStringItem(BString(stamp) << message->GetString("text", "")), 0);
			while (fResults->CountItems() > 100)
				delete fResults->RemoveItem(fResults->CountItems() - 1);
			break;
		}
		default:
			BWindow::MessageReceived(message);
	}
}


/** With a build running, stop it first (it is reported as failed), then quit once the worker has exited. */
bool
MainWindow::QuitRequested()
{
	if (!fWorker.IsRunning())
		return true;
	fQuitting = true;
	fWorker.Stop();
	fState->SetText("State: stopping before quit");
	_UpdateButtons();
	return false;
}


/** The buttons can also be pressed from a shell: hey application/x-vnd.RenkuOS-PortsBuilder 'stop' (or 'strt', 'paus', 'tst_'). */
class App : public BApplication {
public:
	App() : BApplication(APP_SIGNATURE) {}
	void ReadyToRun() override { (fWindow = new MainWindow())->Show(); }
	void MessageReceived(BMessage* message) override
	{
		switch (message->what) {
			case MSG_START: case MSG_PAUSE: case MSG_STOP: case MSG_TEST:
				fWindow->PostMessage(message);
				break;
			default:
				BApplication::MessageReceived(message);
		}
	}

private:
	BWindow* fWindow = NULL;
};


int
main()
{
	App app;
	app.Run();
	return 0;
}
