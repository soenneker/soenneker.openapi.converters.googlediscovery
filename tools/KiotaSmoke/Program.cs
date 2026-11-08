using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Google.Calendar;
using Google.Calendar.Models;
using Google.Meet;
using Google.Meet.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Serialization.Json;

using var meetAdapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider());
using var calendarAdapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider());
var meet = new MeetClient(meetAdapter);
var calendar = new CalendarClient(calendarAdapter);
Check(meet.V2.Spaces["abc-123"].ToGetRequestInformation().URI.AbsoluteUri == "https://meet.googleapis.com/v2/spaces/abc-123", "Meet space URL");
Check(meet.V2.ConferenceRecords["record"].Participants["person"].ParticipantSessions["session"].ToGetRequestInformation().URI.AbsoluteUri ==
    "https://meet.googleapis.com/v2/conferenceRecords/record/participants/person/participantSessions/session", "Nested Meet URL");
Check(meet.V2.Spaces.WithSpacesIdEndActiveConference("abc-123").ToPostRequestInformation(new EndActiveConferenceRequest()).URI.AbsoluteUri ==
    "https://meet.googleapis.com/v2/spaces/abc-123:endActiveConference", "Meet custom action URL");
var list = calendar.Calendars["person@example.com"].Events.ToGetRequestInformation(c =>
{
    c.QueryParameters.MaxResults = 25;
    c.QueryParameters.SingleEvents = true;
    c.QueryParameters.PrivateExtendedProperty = new[] { "a=1", "b=2" };
});
string url = list.URI.AbsoluteUri;
Check(url.StartsWith("https://www.googleapis.com/calendar/v3/calendars/person%40example.com/events?", StringComparison.Ordinal), "Encoded Calendar ID and base path");
Check(url.Contains("maxResults=25") && url.Contains("singleEvents=true"), "Typed query values");
Check(url.Contains("privateExtendedProperty=a%3D1") && url.Contains("privateExtendedProperty=b%3D2"), "Repeated query values");
var create = calendar.Calendars["primary"].Events.ToPostRequestInformation(new Event
{
    Summary = "Converter smoke test",
    Start = new EventDateTime { DateTime = DateTimeOffset.Parse("2026-11-07T10:00:00Z") },
    End = new EventDateTime { DateTime = DateTimeOffset.Parse("2026-11-07T11:00:00Z") }
});
using var body = JsonDocument.Parse(create.Content!);
Check(body.RootElement.GetProperty("summary").GetString() == "Converter smoke test", "Calendar request serialization");
Check(body.RootElement.GetProperty("start").GetProperty("dateTime").GetDateTimeOffset().Hour == 10, "Date-time serialization");
using var channelStream = new MemoryStream(Encoding.UTF8.GetBytes("""{"id":"channel","expiration":"9223372036854775807"}"""));
var channel = (await new JsonParseNodeFactory().GetRootParseNodeAsync("application/json", channelStream)).GetObjectValue(Channel.CreateFromDiscriminatorValue)!;
Check(channel.Expiration == "9223372036854775807", "64-bit string deserialization");
using var recordStream = new MemoryStream(Encoding.UTF8.GetBytes("""{"name":"conferenceRecords/record","startTime":"2026-11-07T10:00:00Z"}"""));
var record = (await new JsonParseNodeFactory().GetRootParseNodeAsync("application/json", recordStream)).GetObjectValue(ConferenceRecord.CreateFromDiscriminatorValue)!;
Check(record.StartTime?.Hour == 10, "Meet timestamp deserialization");
Console.WriteLine("Both generated clients compiled; 10 URL, query, serialization, and deserialization checks passed. No API calls were made.");

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
