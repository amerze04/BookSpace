using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookSpace.IntegrationTests.Support;

// The serializer settings a client of this API needs, mirroring what Program.cs
// configures on the server (WP-3 Phase 3).
//
// Only one setting so far, and it exists because the API serializes enums as
// their names: an availability window's weekday arrives as "Monday", and
// System.Text.Json's default enum converter reads numbers only, so
// ReadFromJsonAsync would throw without this.
//
// Worth having as a shared type rather than an inline options object per call:
// these tests are a real client of the wire contract, and a client that has to
// rediscover the contract at every call site is exactly the thing the OpenAPI
// document and this file exist to prevent.
internal static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
