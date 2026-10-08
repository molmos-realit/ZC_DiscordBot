using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZC.DiscordBot
{
    internal sealed class IncomingCommand { internal string Id, AuthorId, AuthorName, Content; }

    internal sealed class DiscordClient : IDisposable
    {
        private const string Api = "https://discord.com/api/v10";
        private readonly BotSettings settings;
        private readonly HttpClient http;
        private CancellationTokenSource stop;
        private string afterId;
        private bool initialized;
        private bool firstReadablePollLogged;
        internal readonly ConcurrentQueue<IncomingCommand> Commands = new ConcurrentQueue<IncomingCommand>();

        internal DiscordClient(BotSettings value)
        {
            settings = value;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", settings.Token);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZC_DiscordBot/1.2.0");
        }

        internal void Start()
        {
            stop = new CancellationTokenSource();
            Task.Run(() => PollLoop(stop.Token));
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string url = Api + "/channels/" + settings.ChannelId + "/messages?limit=50" + (string.IsNullOrEmpty(afterId) ? "" : "&after=" + afterId);
                    HttpResponseMessage response = await http.GetAsync(url, token).ConfigureAwait(false);
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        SafeLog("consulta Discord HTTP " + (int)response.StatusCode + " body=" + TrimForLog(body));
                        await Delay(token).ConfigureAwait(false);
                        continue;
                    }

                    JArray messages = JArray.Parse(body);
                    JToken[] ordered = messages.OrderBy(x => (string)x["id"]).ToArray();
                    if (!firstReadablePollLogged)
                    {
                        firstReadablePollLogged = true;
                        Log.Out("[ZC_DiscordBot] Lectura Discord OK; mensajes visibles=" + ordered.Length + " prefix='" + settings.Prefix + "'.");
                    }

                    if (!initialized)
                    {
                        if (ordered.Length > 0) afterId = (string)ordered[ordered.Length - 1]["id"];
                        initialized = true;
                        Log.Out("[ZC_DiscordBot] Canal validado; escucha de comandos activa. afterId=" + (string.IsNullOrEmpty(afterId) ? "sin mensajes" : afterId));
                    }
                    else
                    {
                        foreach (JToken m in ordered)
                        {
                            string id = (string)m["id"];
                            if (!string.IsNullOrEmpty(id)) afterId = id;
                            if ((bool?)m["author"]?["bot"] == true) continue;
                            string content = ((string)m["content"] ?? "").Trim();
                            if (string.IsNullOrEmpty(content)) continue;
                            if (!content.StartsWith(settings.Prefix, StringComparison.Ordinal)) continue;

                            IncomingCommand command = new IncomingCommand
                            {
                                Id = id,
                                AuthorId = (string)m["author"]?["id"],
                                AuthorName = (string)m["author"]?["username"] ?? "Usuario",
                                Content = content
                            };
                            Commands.Enqueue(command);
                            Log.Out("[ZC_DiscordBot] Comando recibido: '" + SafeCommand(content) + "' de " + command.AuthorName + ".");
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { SafeLog("lectura Discord: " + ex.GetType().Name + " - " + ex.Message); }
                await Delay(token).ConfigureAwait(false);
            }
        }

        private Task Delay(CancellationToken t)
        {
            return Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), t);
        }

        internal async Task<string> Send(string content)
        {
            try
            {
                JObject data = new JObject();
                data["content"] = Limit(content);
                HttpResponseMessage r = await http.PostAsync(Api + "/channels/" + settings.ChannelId + "/messages", Json(data)).ConfigureAwait(false);
                string b = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!r.IsSuccessStatusCode)
                {
                    SafeLog("envio Discord HTTP " + (int)r.StatusCode + " body=" + TrimForLog(b));
                    return null;
                }
                return (string)JObject.Parse(b)["id"];
            }
            catch (Exception ex) { SafeLog("envio Discord: " + ex.GetType().Name + " - " + ex.Message); return null; }
        }

        internal async Task<bool> Edit(string id, string content)
        {
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                JObject data = new JObject();
                data["content"] = Limit(content);
                HttpRequestMessage q = new HttpRequestMessage(new HttpMethod("PATCH"), Api + "/channels/" + settings.ChannelId + "/messages/" + id);
                q.Content = Json(data);
                HttpResponseMessage r = await http.SendAsync(q).ConfigureAwait(false);
                string b = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!r.IsSuccessStatusCode) SafeLog("edicion Discord HTTP " + (int)r.StatusCode + " body=" + TrimForLog(b));
                return r.IsSuccessStatusCode;
            }
            catch (Exception ex) { SafeLog("edicion Discord: " + ex.GetType().Name + " - " + ex.Message); return false; }
        }

        private static StringContent Json(JObject x)
        {
            return new StringContent(x.ToString(Formatting.None), Encoding.UTF8, "application/json");
        }

        private static string Limit(string x)
        {
            if (string.IsNullOrEmpty(x)) return " ";
            return x.Length <= 2000 ? x : x.Substring(0, 1997) + "...";
        }

        private static string TrimForLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 180 ? text : text.Substring(0, 180) + "...";
        }

        private static string SafeCommand(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= 80 ? text : text.Substring(0, 80) + "...";
        }

        private static void SafeLog(string text)
        {
            Log.Warning("[ZC_DiscordBot] " + text.Replace("Bot ", "[credencial] "));
        }

        public void Dispose()
        {
            if (stop != null) stop.Cancel();
            http.Dispose();
        }
    }
}