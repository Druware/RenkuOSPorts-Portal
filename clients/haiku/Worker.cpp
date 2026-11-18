/*
 * The build loop: claim a job, update the ports tree, run HaikuPorter (or the test build command), upload the
 * packages and complete the job, or report the failure with the log. Runs in its own thread; it talks to the
 * window only through messages.
 * MIT License.
 */
#include "Builder.h"

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <map>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#include <Directory.h>
#include <File.h>
#include <Json.h>
#include <OS.h>

//! How long a stopped build gets after SIGINT (HaikuPorter cleans up its chroot on it) before SIGKILL.
static const bigtime_t kKillGrace = 60 * 1000000LL;


/** The multipart/form-data upload body: sha256 and buildDate fields, then the file, streamed from disk. */
class MultipartBody : public BDataIO {
public:
	MultipartBody(const BString& head, const char* path, const BString& tail)
		: fHead(head), fTail(tail), fFile(path, B_READ_ONLY) {}

	status_t InitCheck() const { return fFile.InitCheck(); }

	ssize_t Read(void* buffer, size_t size) override
	{
		if (fPart == 0) {
			ssize_t n = _Copy(fHead, buffer, size);
			if (n > 0)
				return n;
			fPart = 1;
		}
		if (fPart == 1) {
			ssize_t n = fFile.Read(buffer, size);
			if (n != 0)
				return n;	// data or a read error
			fPart = 2;
			fOffset = 0;
		}
		return _Copy(fTail, buffer, size);
	}

	ssize_t Write(const void*, size_t) override { return B_NOT_SUPPORTED; }

private:
	ssize_t _Copy(const BString& s, void* buffer, size_t size)
	{
		size_t n = std::min(size, (size_t)(s.Length() - fOffset));
		memcpy(buffer, s.String() + fOffset, n);
		fOffset += n;
		return n;
	}

	BString fHead, fTail;
	BFile fFile;
	int fPart = 0;
	size_t fOffset = 0;
};


/** Name to modification time of the *.hpkg files in @p dir. */
static std::map<BString, time_t>
list_hpkgs(const BString& dir)
{
	std::map<BString, time_t> files;
	DIR* d = opendir(dir.String());
	if (d == NULL)
		return files;
	while (dirent* e = readdir(d)) {
		BString name(e->d_name);
		struct stat st;
		if (name.EndsWith(".hpkg") && stat((BString(dir) << "/" << name).String(), &st) == 0
			&& S_ISREG(st.st_mode))
			files[name] = st.st_mtime;
	}
	closedir(d);
	return files;
}


void
Worker::Start(const Settings& settings)
{
	fPause = false;
	if (IsRunning())
		return;
	fSettings = settings;
	fStop = false;
	fThread = spawn_thread(_Main, "build worker", B_NORMAL_PRIORITY, this);
	if (fThread >= 0)
		resume_thread(fThread);
}


void
Worker::Pause()
{
	fPause = true;
}


void
Worker::Stop()
{
	fStop = true;
	pid_t child = fChild;
	if (child > 0)
		kill(-child, SIGINT);
	fApi.Cancel();
}


void
Worker::WaitForExit()
{
	if (fThread >= 0) {
		status_t ignored;
		wait_for_thread(fThread, &ignored);
		fThread = -1;
	}
}


int32
Worker::_Main(void* self)
{
	Worker* worker = (Worker*)self;
	worker->_Loop();
	worker->fTarget.SendMessage(MSG_WORKER_EXITED);
	return 0;
}


void
Worker::_Loop()
{
	fApi.Configure(fSettings.server, fSettings.clientId, fSettings.secret);
	_Log("RenkuPorts Builder %s: polling %s every %" B_PRId32 " s%s", APP_VERSION,
		fSettings.server.String(), fSettings.pollSeconds,
		fSettings.buildCommand.IsEmpty() ? "" : " (test build command)");
	while (!_Stopping()) {
		_State("connecting");
		BString summary, error;
		if (fApi.Summary(summary, error) == B_OK) {
			BMessage queue(MSG_QUEUE);
			queue.AddString("text", summary);
			fTarget.SendMessage(&queue, (BHandler*)NULL, 1000000);
		}
		HttpResponse reply;
		if (fApi.Call("POST", "api/client/jobs/next", reply, error) != B_OK) {
			if (fStop)
				break;
			_State("error: %s", error.String());
			_Log("error: %s", error.String());
			_Sleep(fSettings.pollSeconds);
			continue;
		}
		BMessage job;
		if (reply.status == 204) {
			_State("connected, idle (no job queued)");
			_Sleep(fSettings.pollSeconds);
		} else if (BJson::Parse(reply.body, job) != B_OK || !job.HasDouble("jobId")) {
			_State("error: unreadable job");
			_Log("error: unreadable reply to jobs/next: %.300s", reply.body.String());
			_Sleep(fSettings.pollSeconds);
		} else {
			_ProcessJob(job);
			BMessage none(MSG_JOB);
			none.AddString("job", "");
			fTarget.SendMessage(&none, (BHandler*)NULL, 1000000);
		}
	}
	_State(fStop ? "stopped" : "paused");
	_Log(fStop ? "stopped" : "paused");
}


void
Worker::_ProcessJob(const BMessage& job)
{
	int64 id = (int64)job.GetDouble("jobId", 0);
	BString port = job.GetString("portName", ""), version = job.GetString("version", "");
	BString platform = job.GetString("platform", ""), recipe = job.GetString("recipePath", "");
	BString name = BString(port) << "-" << version;

	BMessage started(MSG_JOB);
	started.AddString("job", BString(name) << " (job " << id << ")");
	started.AddInt64("started", real_time_clock());
	fTarget.SendMessage(&started, (BHandler*)NULL, 1000000);
	BMessage clear(MSG_LOG);
	clear.AddBool("clear", true);
	fTarget.SendMessage(&clear, (BHandler*)NULL, 1000000);
	_State("building %s", name.String());

	BString dir = BString(fSettings.workDir) << "/job-" << id;
	BString outDir = BString(dir) << "/out", logPath = BString(dir) << "/build.log";
	_Run({"rm", "-rf", dir}, {}, nullptr, false);
	create_directory(outDir.String(), 0755);
	fLogFd = open(logPath.String(), O_WRONLY | O_CREAT | O_TRUNC | O_APPEND, 0644);
	// "build": the job's build number, from servers that verify each job with two matching builds.
	BString buildNumber;
	if (job.HasDouble("build"))
		buildNumber << ", build " << (int32)job.GetDouble("build", 0);
	_Log("job %" B_PRId64 ": building %s for %s (recipe %s%s)", id, name.String(), platform.String(),
		recipe.String(), buildNumber.String());

	bool fake = !fSettings.buildCommand.IsEmpty();
	BString tree = fSettings.treePath, packages = BString(tree) << "/packages";
	std::vector<BString> env = {BString("RENKUPORTS_PLATFORM=") << platform,
		BString("RENKUPORTS_JOB_ID=") << id, BString("RENKUPORTS_RECIPE_PATH=") << recipe};
	std::vector<BString> argv;
	std::map<BString, time_t> before;
	if (fake) {
		argv = {fSettings.buildCommand, port, version, outDir};
	} else {
		// The job names no commit, so build the tip of the checkout's branch: fast-forward only, never discard
		// local changes. A failed update is logged and the checkout is built as it is.
		if (access((BString(tree) << "/.git").String(), F_OK) == 0) {
			if (_Run({"git", "-C", tree, "pull", "--ff-only"}, {}) != 0)
				_Log("warning: git pull --ff-only failed; building the ports tree as it is");
			BString head;
			_Run({"git", "-C", tree, "rev-parse", "HEAD"}, {}, &head);
			_Log("ports tree %s at commit %s", tree.String(), head.Trim().String());
		} else
			_Log("ports tree %s is not a git checkout; building it as it is", tree.String());
		if (access((BString(tree) << "/" << recipe).String(), R_OK) != 0)
			_Log("warning: recipe %s not found in the ports tree", recipe.String());
		before = list_hpkgs(packages);
		argv = {fSettings.haikuporter, BString("--config=") << fSettings.haikuporterConfig, "-y",
			"--no-source-packages", "--get-dependencies", BString("-j") << fSettings.jobs, name};
	}
	BString line("running:");
	for (const BString& a : argv)
		line << " " << a;
	_Log("%s", line.String());
	int exitCode = _Run(argv, env);

	// The packages to upload: the test command's out dir, or what HaikuPorter added to or rewrote in the
	// tree's packages/ (the port's packages and any dependencies built in the same run).
	std::vector<BString> files;
	BString source = fake ? outDir : packages;
	for (const auto& [file, mtime] : list_hpkgs(source)) {
		auto old = before.find(file);
		if (!file.EndsWith("-source.hpkg") && (old == before.end() || old->second != mtime))
			files.push_back(BString(source) << "/" << file);
	}
	if (!fake && files.empty() && exitCode == 0) {
		// HaikuPorter built nothing because the packages exist: take this port version's, as build-stack-arm64.sh does.
		BString main = BString(port) << "-" << version << "-", sub = BString(port) << "_";
		BString subVersion = BString("-") << version << "-";
		for (const auto& [file, mtime] : before)
			if (!file.EndsWith("-source.hpkg")
				&& (file.StartsWith(main) || (file.StartsWith(sub) && file.FindFirst(subVersion) > 0)))
				files.push_back(BString(packages) << "/" << file);
	}

	BString summary;
	if (fStop)
		summary = "client stopped";
	else if (exitCode != 0)
		summary.SetToFormat("build command exited with code %d", exitCode);
	else if (files.empty())
		summary = "build produced no .hpkg";
	for (size_t i = 0; summary.IsEmpty() && i < files.size(); i++) {
		const BString& path = files[i];
		BString file = path.String() + path.FindLast('/') + 1, sha, error;
		struct stat st;
		if (_Run({"sha256sum", "--", path}, {}, &sha) != 0 || sha.Length() < 64 || stat(path.String(), &st) != 0) {
			summary.SetToFormat("cannot hash %s", file.String());
			break;
		}
		sha.Truncate(64);
		char date[32];
		struct tm tm;
		strftime(date, sizeof(date), "%Y-%m-%dT%H:%M:%SZ", gmtime_r(&st.st_mtime, &tm));
		BString boundary;
		boundary.SetToFormat("renkuports-%" B_PRIx64, (uint64)system_time());
		// The server reads sha256 and buildDate before the file part, so they come first.
		BString head, tail;
		head << "--" << boundary << "\r\nContent-Disposition: form-data; name=\"sha256\"\r\n\r\n" << sha
			<< "\r\n--" << boundary << "\r\nContent-Disposition: form-data; name=\"buildDate\"\r\n\r\n" << date
			<< "\r\n--" << boundary << "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" << file
			<< "\"\r\nContent-Type: application/octet-stream\r\n\r\n";
		tail << "\r\n--" << boundary << "--\r\n";
		BString path_(path);
		HttpResponse reply;
		_Log("uploading %s (%" B_PRIdOFF " bytes, sha256 %s)", file.String(), st.st_size, sha.String());
		status_t status = fApi.Call("POST", BString("api/client/jobs/") << id << "/packages", reply, error,
			[&](off_t& size, BString& type) -> BDataIO* {
				MultipartBody* body = new MultipartBody(head, path_, tail);
				if (body->InitCheck() != B_OK) {
					delete body;
					type.SetToFormat("cannot open %s", path_.String());
					return NULL;
				}
				size = head.Length() + st.st_size + tail.Length();
				type = BString("multipart/form-data; boundary=") << boundary;
				return body;
			});
		if (fStop)
			summary = "client stopped";
		else if (status != B_OK)
			summary = BString("upload failed: ") << error;
		else
			_Log("uploaded %s", file.String());
	}

	if (summary.IsEmpty()) {
		BString log = ReadLogTail(logPath), error;
		BString json = BString("{\"log\":") << JsonString(log, log.Length()) << "}";
		HttpResponse reply;
		if (fApi.PostJson(BString("api/client/jobs/") << id << "/complete", json, reply, error) == B_OK) {
			// Succeeded, or Verifying while the server waits for a second matching build.
			BMessage done;
			BString jobStatus = BJson::Parse(reply.body, done) == B_OK ? done.GetString("status", "?") : "?";
			_Log("job %" B_PRId64 ": completed, %zu package(s) uploaded, job is %s", id, files.size(),
				jobStatus.String());
			BMessage result(MSG_RESULT);
			result.AddString("text", BString(name) << " (job " << id << "): completed, " << (int32)files.size()
				<< " package(s), job " << jobStatus);
			fTarget.SendMessage(&result, (BHandler*)NULL, 1000000);
		} else
			summary = BString("upload failed: ") << error;
	}
	if (!summary.IsEmpty()) {
		_Fail(id, summary, logPath);
		BMessage result(MSG_RESULT);
		result.AddString("text", BString(name) << " (job " << id << "): failed: " << summary);
		fTarget.SendMessage(&result, (BHandler*)NULL, 1000000);
	}
	close(fLogFd);
	fLogFd = -1;
	_Run({"rm", "-rf", outDir}, {}, nullptr, false);	// hpkgs can be large; build.log stays
}


/** Reports the failure. Runs after Stop too, so a stopped client does not leave its job claimed. */
bool
Worker::_Fail(int64 jobId, const BString& summary, const BString& logPath)
{
	_Log("job %" B_PRId64 ": failed: %s", jobId, summary.String());
	BString log = ReadLogTail(logPath), error;
	BString json = BString("{\"summary\":") << JsonString(summary, summary.Length()) << ",\"log\":"
		<< JsonString(log, log.Length()) << "}";
	HttpResponse reply;
	if (fApi.PostJson(BString("api/client/jobs/") << jobId << "/fail", json, reply, error) != B_OK) {
		_Log("job %" B_PRId64 ": reporting the failure failed (%s); the job stays claimed until its lease expires",
			jobId, error.String());
		return false;
	}
	return true;
}


/**
 * Runs @p argv in its own process group with stdin from /dev/null and stdout+stderr into the build log and the
 * window (or, with @p capture, into that string only). Stop sends the group SIGINT, then SIGKILL after the grace
 * period (unless @p stoppable is false, for clean-up commands). Returns the exit code (128 + signal if killed, 127 if it could not be started).
 */
int
Worker::_Run(const std::vector<BString>& argv, const std::vector<BString>& env, BString* capture,
	bool stoppable)
{
	int fds[2];
	if (pipe(fds) != 0)
		return 127;
	pid_t pid = fork();
	if (pid < 0) {
		close(fds[0]);
		close(fds[1]);
		return 127;
	}
	if (pid == 0) {
		setpgid(0, 0);
		int null = open("/dev/null", O_RDONLY);
		dup2(null, 0);
		dup2(fds[1], 1);
		dup2(fds[1], 2);
		close(fds[0]);
		close(fds[1]);
		for (const BString& e : env)
			putenv(strdup(e.String()));
		std::vector<char*> args;
		for (const BString& a : argv)
			args.push_back((char*)a.String());
		args.push_back(NULL);
		execvp(args[0], args.data());
		fprintf(stderr, "cannot run %s: %s\n", args[0], strerror(errno));
		_exit(127);
	}
	setpgid(pid, pid);	// also here, so a Stop right after fork already reaches the group
	close(fds[1]);
	fChild = pid;
	fcntl(fds[0], F_SETFL, O_NONBLOCK);

	bool reading = true, exited = false;
	bigtime_t interruptedAt = 0;
	int status = 0;
	char buffer[65536];
	while (!exited) {
		if (reading) {
			pollfd p = {fds[0], POLLIN, 0};
			poll(&p, 1, 500);
			// Drain what is there; EOF only means the output closed (a grandchild may still run, or hold it).
			ssize_t n;
			while ((n = read(fds[0], buffer, sizeof(buffer))) > 0) {
				if (capture != nullptr)
					capture->Append(buffer, n);
				else {
					if (fLogFd >= 0)
						write(fLogFd, buffer, n);
					fPending.Append(buffer, n);
				}
			}
			if (n == 0 || (n < 0 && errno != EAGAIN && errno != EINTR))
				reading = false;
		} else
			snooze(200000);
		if (capture == nullptr && (fPending.Length() > 32768 || system_time() - fFlushedAt > 300000))
			_Flush();
		if (waitpid(pid, &status, WNOHANG) == pid)
			exited = true;
		else if (fStop && stoppable) {
			if (interruptedAt == 0) {
				_Log("stopping the build");
				kill(-pid, SIGINT);
				interruptedAt = system_time();
			} else if (system_time() - interruptedAt > kKillGrace)
				kill(-pid, SIGKILL);
		}
	}
	// Whatever the exited process wrote last; a lingering grandchild's output is not waited for.
	ssize_t n;
	while (reading && (n = read(fds[0], buffer, sizeof(buffer))) > 0) {
		if (capture != nullptr)
			capture->Append(buffer, n);
		else {
			if (fLogFd >= 0)
				write(fLogFd, buffer, n);
			fPending.Append(buffer, n);
		}
	}
	close(fds[0]);
	fChild = -1;
	_Flush();
	return WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
}


void
Worker::_Flush()
{
	fFlushedAt = system_time();
	if (fPending.IsEmpty())
		return;
	BMessage message(MSG_LOG);
	message.AddString("text", fPending);
	fTarget.SendMessage(&message, (BHandler*)NULL, 1000000);
	fPending = "";
}


/** A timestamped client message: to stdout, the build log of the current job and the window. */
void
Worker::_Log(const char* format, ...)
{
	char text[2048], line[2100], stamp[32];
	va_list args;
	va_start(args, format);
	vsnprintf(text, sizeof(text), format, args);
	va_end(args);
	time_t now = time(NULL);
	struct tm tm;
	strftime(stamp, sizeof(stamp), "%Y-%m-%d %H:%M:%S", localtime_r(&now, &tm));
	int n = snprintf(line, sizeof(line), "%s %s\n", stamp, text);
	n = std::min(n, (int)sizeof(line) - 1);
	fputs(line, stdout);
	fflush(stdout);
	if (fLogFd >= 0)
		write(fLogFd, line, n);
	fPending.Append(line, n);
	_Flush();
}


void
Worker::_State(const char* format, ...)
{
	char text[1024];
	va_list args;
	va_start(args, format);
	vsnprintf(text, sizeof(text), format, args);
	va_end(args);
	BMessage message(MSG_STATE);
	message.AddString("state", text);
	fTarget.SendMessage(&message, (BHandler*)NULL, 1000000);
}


void
Worker::_Sleep(int32 seconds)
{
	for (int32 i = 0; i < seconds * 4 && !_Stopping(); i++)
		snooze(250000);
}
