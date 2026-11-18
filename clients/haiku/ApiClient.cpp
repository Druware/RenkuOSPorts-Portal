/*
 * HTTP client for the coordinator's build client API.
 *
 * Uses the legacy network services kit (libnetservices.a, the one HaikuDepot uses): it streams a request body
 * from any BDataIO with a caller-set Content-Type, which a multipart upload needs for its boundary.
 * netservices2 cannot do that (BHttpRequest::SetRequestBody rejects a MIME type with parameters), and libcurl's
 * headers are not part of a base install.
 * MIT License.
 */
#include "Builder.h"

#include <string.h>

#include <HttpHeaders.h>
#include <HttpRequest.h>
#include <HttpResult.h>
#include <Json.h>
#include <Url.h>
#include <UrlProtocolRoster.h>

using namespace BPrivate::Network;

//! Tokens live 12 h on the server; get a new one an hour before (a 401 also refreshes it).
static const bigtime_t kTokenRefresh = 11LL * 3600 * 1000000;


void
ApiClient::Configure(const BString& server, const BString& clientId, const BString& secret)
{
	fServer = server;
	fServer.Trim();
	if (!fServer.EndsWith("/"))
		fServer << "/";
	fClientId = clientId;
	fClientId.Trim();
	fSecret = secret;
	fSecret.Trim();
	fToken = "";
}


/** One HTTP exchange. Takes ownership of @p body. Returns B_OK when a response (any status) arrived. */
status_t
ApiClient::_Send(const char* method, const BString& url, const BString* token, HttpResponse& out,
	BDataIO* body, off_t size, const BString& type)
{
	out = HttpResponse();
	BMallocIO output;
	BUrl u(url.String(), false);
	BHttpRequest* request = u.IsValid()
		? dynamic_cast<BHttpRequest*>(BUrlProtocolRoster::MakeRequest(u, &output)) : NULL;
	if (request == NULL) {
		delete body;
		return B_BAD_VALUE;
	}
	request->SetMethod(method);
	// A redirect would turn a POST into a GET without its body; report it instead (e.g. http:// given for an https site).
	request->SetFollowLocation(false);
	request->SetUserAgent("renkuports-builder/" APP_VERSION);
	BHttpHeaders headers;
	if (token != NULL)
		headers.AddHeader("Authorization", (BString("Bearer ") << *token).String());
	if (body == NULL && strcmp(method, "POST") == 0) {
		body = new BMallocIO();	// "Content-Length: 0" rather than no length at all
		size = 0;
	}
	if (body != NULL) {
		headers.AddHeader("Content-Type", type.IsEmpty() ? "text/plain" : type.String());
		request->AdoptInputData(body, size);
	}
	request->SetHeaders(headers);

	fLock.Lock();
	fCurrent = request;
	fLock.Unlock();
	// shortcut: the legacy kit sets no socket timeout, so a server that stops answering mid-request hangs this
	// thread until Stop (which cancels the request); add a watchdog if that shows up in practice.
	thread_id thread = request->Run();
	if (thread >= 0) {
		status_t ignored;
		wait_for_thread(thread, &ignored);
	}
	fLock.Lock();
	fCurrent = nullptr;
	fLock.Unlock();

	out.status = dynamic_cast<const BHttpResult&>(request->Result()).StatusCode();
	out.body.SetTo((const char*)output.Buffer(), output.BufferLength());
	status_t status = thread < 0 ? thread : request->Status();
	delete request;
	if (out.status != 0)
		return B_OK;
	return status != B_OK ? status : B_ERROR;
}


void
ApiClient::Cancel()
{
	fLock.Lock();
	if (fCurrent != nullptr)
		((BHttpRequest*)fCurrent)->Stop();
	fLock.Unlock();
}


status_t
ApiClient::_Token(BString& error)
{
	if (!fToken.IsEmpty() && system_time() - fTokenAt < kTokenRefresh)
		return B_OK;
	BString json;
	json << "{\"clientId\":" << JsonString(fClientId, fClientId.Length())
		<< ",\"secret\":" << JsonString(fSecret, fSecret.Length()) << "}";
	BMallocIO* body = new BMallocIO();
	body->Write(json.String(), json.Length());
	HttpResponse out;
	status_t status = _Send("POST", BString(fServer) << "api/client/token", nullptr, out, body,
		json.Length(), "application/json");
	if (status != B_OK) {
		error.SetToFormat("cannot reach %s: %s", fServer.String(), strerror(status));
		return status;
	}
	BMessage reply;
	const char* token;
	if (out.status != 200 || BJson::Parse(out.body, reply) != B_OK
		|| reply.FindString("accessToken", &token) != B_OK) {
		error.SetToFormat("token request failed: %" B_PRId32 " %.300s", out.status, out.body.String());
		return B_NOT_ALLOWED;
	}
	fToken = token;
	fTokenAt = system_time();
	return B_OK;
}


status_t
ApiClient::Call(const char* method, const char* path, HttpResponse& out, BString& error,
	std::function<BDataIO*(off_t& size, BString& type)> makeBody)
{
	for (int attempt = 0; ; attempt++) {
		status_t status = _Token(error);
		if (status != B_OK)
			return status;
		off_t size = 0;
		BString type;
		BDataIO* body = NULL;
		if (makeBody) {
			body = makeBody(size, type);
			if (body == NULL) {
				error = type;	// makeBody puts its error text there
				return B_ERROR;
			}
		}
		status = _Send(method, BString(fServer) << path, &fToken, out, body, size, type);
		if (status != B_OK) {
			error.SetToFormat("%s %s: %s", method, path, strerror(status));
			return status;
		}
		if (out.status == 401 && attempt == 0) {
			fToken = "";
			continue;
		}
		if (out.status >= 200 && out.status < 300)
			return B_OK;
		error.SetToFormat("%s %s: %" B_PRId32 " %.500s", method, path, out.status, out.body.String());
		return B_ERROR;
	}
}


status_t
ApiClient::PostJson(const char* path, const BString& json, HttpResponse& out, BString& error)
{
	return Call("POST", path, out, error, [&json](off_t& size, BString& type) -> BDataIO* {
		BMallocIO* body = new BMallocIO();
		body->Write(json.String(), json.Length());
		size = json.Length();
		type = "application/json";
		return body;
	});
}


status_t
ApiClient::Summary(BString& text, BString& error)
{
	HttpResponse out;
	BMessage me, page;
	status_t status = Call("GET", "api/client/me", out, error);
	if (status != B_OK)
		return status;
	if (BJson::Parse(out.body, me) != B_OK) {
		error = "api/client/me: unreadable reply";
		return B_BAD_DATA;
	}
	BString platform = me.GetString("platform", "?");
	text.SetToFormat("%s (%s, owner %s)", me.GetString("name", "?"), platform.String(),
		me.GetString("owner", "?"));

	BString path("api/client/jobs?status=Queued&pageSize=1&platform=");
	path << platform;
	if (Call("GET", path, out, error) == B_OK && BJson::Parse(out.body, page) == B_OK)
		text << ": " << (int64)page.GetDouble("total", 0) << " job(s) queued for " << platform;

	BMessage job;
	if (me.FindMessage("currentJob", &job) == B_OK) {
		text << "; building " << job.GetString("portName", "?") << "-" << job.GetString("version", "?")
			<< " (job " << (int64)job.GetDouble("id", 0) << ", attempt "
			<< (int32)job.GetDouble("attempts", 0);
		if (job.HasDouble("unblocks"))
			text << ", unblocks " << (int32)job.GetDouble("unblocks", 0) << " queued";
		text << ")";
	} else
		text << "; no job claimed";
	return B_OK;
}
