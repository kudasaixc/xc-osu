// xc-osu! server — Newtonsoft responses so osu!'s own models serialise exactly as the client expects. xc softworks (kudasaixc)

using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XcOsu.Server.Api
{
    public static class Json
    {
        public static readonly JsonSerializerSettings SETTINGS = new JsonSerializerSettings
        {
            MaxDepth = 32,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            NullValueHandling = NullValueHandling.Include,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            ContractResolver = new ServerContractResolver(),
        };

        /// <summary>
        /// osu!'s client models hide server-provided fields (id, user, pp…) via ShouldSerialize* so they're not sent on submission.
        /// The server is the side that must send them.
        /// </summary>
        private sealed class ServerContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            protected override Newtonsoft.Json.Serialization.JsonProperty CreateProperty(System.Reflection.MemberInfo member, MemberSerialization memberSerialization)
            {
                var p = base.CreateProperty(member, memberSerialization);
                if (member.DeclaringType == typeof(osu.Game.Online.API.Requests.Responses.SoloScoreInfo)
                    && p.PropertyName is "id" or "user" or "beatmap" or "pp" or "has_replay" or "preserve" or "processed")
                    p.ShouldSerialize = null;
                return p;
            }
        }

        public static IResult Ok(object? value, int status = 200) =>
            Results.Content(JsonConvert.SerializeObject(value, SETTINGS), "application/json", Encoding.UTF8, status);

        public static IResult Error(string message, int status = 400) => Ok(new { error = message, authentication = status == 401 ? "basic" : null }, status);

        public static async Task<T?> Body<T>(HttpRequest req)
        {
            using var reader = new StreamReader(req.Body);
            string s = await ReadBounded(reader);
            return string.IsNullOrWhiteSpace(s) ? default : JsonConvert.DeserializeObject<T>(s, SETTINGS);
        }

        private static async Task<string> ReadBounded(StreamReader reader)
        {
            char[] buffer = new char[262145];
            int count = await reader.ReadBlockAsync(buffer);
            if (count > 262144) throw new BadHttpRequestException("Request too large", 413);
            return new string(buffer, 0, count);
        }

        /// <summary>Reads a parameter from the query, a form body or a JSON body (osu! sends all three).</summary>
        public static async Task<Func<string, string?>> Params(HttpRequest req)
        {
            JObject? json = null;
            IFormCollection? form = null;

            if (req.HasFormContentType)
                form = await req.ReadFormAsync();
            else if (req.ContentType?.Contains("json") == true)
            {
                req.EnableBuffering();
                using var reader = new StreamReader(req.Body, leaveOpen: true);
                string s = await ReadBounded(reader);
                req.Body.Position = 0;
                try { json = string.IsNullOrWhiteSpace(s) ? null : JObject.Parse(s); }
                catch (JsonException) { }
            }

            return key =>
            {
                if (form != null && form.TryGetValue(key, out var f)) return f.ToString();
                if (json?[key] is JToken t) return t.Type == JTokenType.String ? t.Value<string>() : t.ToString();
                if (req.Query.TryGetValue(key, out var q)) return q.ToString();
                return null;
            };
        }
    }
}
