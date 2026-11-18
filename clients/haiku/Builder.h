/*
 * RenkuPorts Builder: a native RenkuOS/Haiku build client for the RenkuOSPorts coordinator.
 * Shared declarations of the settings, the HTTP client, the build worker and the window.
 * MIT License.
 */
#ifndef RENKUPORTS_BUILDER_H
#define RENKUPORTS_BUILDER_H

#include <atomic>
#include <functional>
#include <vector>

#include <DataIO.h>
#include <Locker.h>
#include <Message.h>
#include <Messenger.h>
#include <String.h>

#define APP_SIGNATURE "application/x-vnd.RenkuOS-PortsBuilder"
#define APP_VERSION "0.1.0"

/** Messages from the worker (and the connection test) to the window. */
enum {
	MSG_STATE = 'stat',		//!< "state": connected/idle/building/paused/stopped/error text
	MSG_JOB = 'job_',		//!< "job": port-version (empty when none), "started": real_time() seconds
	MSG_LOG = 'log_',		//!< "text": build output or client messages, "clear": start of a new job
	MSG_RESULT = 'rslt',	//!< "text": one line for the recent results list
	MSG_QUEUE = 'queu',		//!< "text": queue information from /api/client/me
	MSG_TEST_DONE = 'test',	//!< "text": result of Test connection
	MSG_WORKER_EXITED = 'wexi'
};


/** Settings, stored as key=value lines in ~/config/settings/RenkuPortsBuilder (mode 0600: it holds the secret). */
struct Settings {
	BString server = "http://localhost:8080/";
	BString clientId;
	BString secret;
	BString treePath = "/boot/home/haikuports";
	BString haikuporter = "haikuporter";
	BString haikuporterConfig = "/boot/home/config/settings/haikuports.conf";
	int32 jobs = 4;
	BString workDir = "/boot/home/renkuports-builder-work";
	bool runOnStart = false;
	//! Hidden (no field in the window): "<cmd> <port> <version> <outdir>" instead of HaikuPorter, for testing.
	BString buildCommand;
	int32 pollSeconds = 60;

	status_t Load();
	status_t Save() const;
};


struct HttpResponse {
	int32 status = 0;	//!< HTTP status, 0 if no response arrived
	BString body;
};


/**
 * The coordinator's client API over Haiku's (legacy) network services kit, with the same token handling as the
 * .NET client: a token is fetched on first use and refreshed before its 12 h lifetime ends; a 401 gets a new
 * token and the request is retried once.
 */
class ApiClient {
public:
	void Configure(const BString& server, const BString& clientId, const BString& secret);

	/**
	 * Sends a request with the bearer token. @p makeBody creates the request body (it is called again for the
	 * 401 retry); it may be empty. Returns B_OK on 2xx, otherwise an error with @p error describing it.
	 */
	status_t Call(const char* method, const char* path, HttpResponse& out, BString& error,
		std::function<BDataIO*(off_t& size, BString& type)> makeBody = nullptr);
	status_t PostJson(const char* path, const BString& json, HttpResponse& out, BString& error);

	/** One line about this client and the queue (GET api/client/me and api/client/jobs). */
	status_t Summary(BString& text, BString& error);

	/** Aborts the request in flight, if any (from another thread). */
	void Cancel();

private:
	status_t _Send(const char* method, const BString& url, const BString* token, HttpResponse& out,
		BDataIO* body, off_t size, const BString& type);
	status_t _Token(BString& error);

	BString fServer, fClientId, fSecret, fToken;
	bigtime_t fTokenAt = 0;
	BLocker fLock;			//!< guards fCurrent
	void* fCurrent = nullptr;	//!< the BHttpRequest in flight
};


/** Runs the poll / build / report loop in its own thread. */
class Worker {
public:
	explicit Worker(BMessenger target) : fTarget(target) {}

	void Start(const Settings& settings);	//!< starts the loop, or un-pauses it
	void Pause();	//!< finish the current job, then stop claiming
	void Stop();	//!< kill the running build, report it failed ("client stopped"), stop
	bool IsRunning() const { return fThread >= 0; }
	void WaitForExit();

private:
	static int32 _Main(void* self);
	void _Loop();
	void _ProcessJob(const BMessage& job);
	bool _Fail(int64 jobId, const BString& summary, const BString& logPath);
	int _Run(const std::vector<BString>& argv, const std::vector<BString>& env,
		BString* capture = nullptr, bool stoppable = true);
	void _Flush();
	void _Log(const char* format, ...);
	void _State(const char* format, ...);
	void _Sleep(int32 seconds);
	bool _Stopping() const { return fStop || fPause; }

	BMessenger fTarget;
	Settings fSettings;
	ApiClient fApi;
	thread_id fThread = -1;
	std::atomic<bool> fStop{false}, fPause{false};
	std::atomic<pid_t> fChild{-1};	//!< process group of the running build
	int fLogFd = -1;	//!< the current job's build.log
	BString fPending;	//!< build output not yet sent to the window
	bigtime_t fFlushedAt = 0;
};


BString JsonString(const char* text, size_t length);
BString ReadLogTail(const char* path);

#endif
