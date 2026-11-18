/*
 * Settings file and small text helpers.
 * MIT License.
 */
#include "Builder.h"

#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#include <FindDirectory.h>
#include <Path.h>


static BString
settings_path()
{
	BPath path;
	if (find_directory(B_USER_SETTINGS_DIRECTORY, &path, true) != B_OK)
		path.SetTo("/boot/home/config/settings");
	path.Append("RenkuPortsBuilder");
	return path.Path();
}


status_t
Settings::Load()
{
	FILE* f = fopen(settings_path().String(), "r");
	if (f == NULL)
		return B_ENTRY_NOT_FOUND;
	char line[4096];
	while (fgets(line, sizeof(line), f) != NULL) {
		line[strcspn(line, "\r\n")] = '\0';
		char* eq = strchr(line, '=');
		if (eq == NULL || line[0] == '#')
			continue;
		*eq = '\0';
		BString key(line), value(eq + 1);
		if (key == "server") server = value;
		else if (key == "client_id") clientId = value;
		else if (key == "secret") secret = value;
		else if (key == "tree_path") treePath = value;
		else if (key == "haikuporter") haikuporter = value;
		else if (key == "haikuporter_config") haikuporterConfig = value;
		else if (key == "jobs") jobs = atoi(value.String());
		else if (key == "work_dir") workDir = value;
		else if (key == "run_on_start") runOnStart = value == "true";
		else if (key == "build_command") buildCommand = value;
		else if (key == "poll_seconds") pollSeconds = atoi(value.String());
	}
	fclose(f);
	if (jobs < 1) jobs = 1;
	if (pollSeconds < 5) pollSeconds = 5;
	return B_OK;
}


/** Writes a temporary file created with mode 0600 and renames it over the old one, so the secret is never world-readable. */
status_t
Settings::Save() const
{
	BString path = settings_path(), tmp = path;
	tmp << ".tmp";
	int fd = open(tmp.String(), O_WRONLY | O_CREAT | O_TRUNC, 0600);
	if (fd < 0)
		return errno;
	fchmod(fd, 0600);	// in case the temp file existed with other bits
	FILE* f = fdopen(fd, "w");
	// Values are single lines; strip newlines a paste may have brought in.
	auto line = [f](const char* key, BString value) {
		value.RemoveAll("\n").RemoveAll("\r");
		fprintf(f, "%s=%s\n", key, value.String());
	};
	line("server", server);
	line("client_id", clientId);
	line("secret", secret);
	line("tree_path", treePath);
	line("haikuporter", haikuporter);
	line("haikuporter_config", haikuporterConfig);
	line("jobs", BString() << jobs);
	line("work_dir", workDir);
	line("run_on_start", runOnStart ? "true" : "false");
	if (!buildCommand.IsEmpty())
		line("build_command", buildCommand);
	if (pollSeconds != 60)
		line("poll_seconds", BString() << pollSeconds);
	bool ok = fflush(f) == 0 && fsync(fd) == 0;
	ok = fclose(f) == 0 && ok;
	if (!ok || rename(tmp.String(), path.String()) != 0) {
		unlink(tmp.String());
		return B_IO_ERROR;
	}
	return B_OK;
}


/**
 * A JSON string literal. Control characters are escaped and bytes that are not valid UTF-8 become '?', since the
 * server's JSON parser rejects invalid UTF-8 (a log tail can start in the middle of a character).
 */
BString
JsonString(const char* text, size_t length)
{
	BString out("\"");
	const unsigned char* s = (const unsigned char*)text;
	for (size_t i = 0; i < length; i++) {
		unsigned char c = s[i];
		if (c == '"' || c == '\\') {
			out << '\\' << (char)c;
		} else if (c < 0x20) {
			char esc[8];
			snprintf(esc, sizeof(esc), "\\u%04x", c);
			out << esc;
		} else if (c < 0x80) {
			out << (char)c;
		} else {
			// shortcut: overlong 3/4-byte forms and surrogates are not rejected; add if the server ever refuses a log.
			size_t n = c >= 0xc2 && c < 0xe0 ? 2 : c >= 0xe0 && c < 0xf0 ? 3 : c >= 0xf0 && c < 0xf5 ? 4 : 0;
			bool valid = n > 0 && i + n <= length;
			for (size_t k = 1; valid && k < n; k++)
				valid = (s[i + k] & 0xc0) == 0x80;
			if (valid) {
				out.Append(text + i, n);
				i += n - 1;
			} else
				out << '?';
		}
	}
	out << '"';
	return out;
}


/** The last 5 MB of the build log (the end is where the error is). */
BString
ReadLogTail(const char* path)
{
	const off_t kMax = 5 << 20;
	BString text;
	int fd = open(path, O_RDONLY);
	if (fd < 0)
		return text;
	off_t size = lseek(fd, 0, SEEK_END);
	off_t start = size > kMax ? size - kMax : 0;
	char* buffer = text.LockBuffer(size - start);
	ssize_t n = pread(fd, buffer, size - start, start);
	text.UnlockBuffer(n > 0 ? n : 0);
	close(fd);
	return text;
}
