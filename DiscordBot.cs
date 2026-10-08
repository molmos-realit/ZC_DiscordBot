using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

namespace ZC.DiscordBot
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            try
            {
                BotManager.Initialize(mod.Path);
                new Harmony("zc.discordbot.v120.optionalintegrations").PatchAll(Assembly.GetExecutingAssembly());
                Log.Out("[ZC_DiscordBot] v1.2.0 cargado (7DTD V3.3.0); integraciones opcionales; el token nunca se escribe en logs.");
            }
            catch (Exception ex)
            {
                Log.Error("[ZC_DiscordBot] Inicio: " + BotManager.Redact(ex.ToString()));
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), "Update")]
    internal static class GameUpdatePatch
    {
        private static void Postfix()
        {
            // Nunca dejar que un error del bot se propague al bucle principal del juego.
            try
            {
                BotManager.Update();
            }
            catch (Exception ex)
            {
                BotManager.LogUpdateError(ex);
            }
        }
    }

    internal static class BotManager
    {
        private static BotSettings cfg;
        private static DiscordClient discord;
        private static string modPath;
        private static string rankingMessageId;
        private static DateTime nextRankingUtc = DateTime.MinValue;
        private static DateTime lastRankingRepostUtc = DateTime.MinValue;
        private static DateTime lastTickUtc = DateTime.MinValue;
        private static readonly HashSet<string> warningsSent = new HashSet<string>();
        private static readonly HashSet<string> announcedKilledBosses = new HashSet<string>();
        private static bool onlineSent;
        private static bool integrationsLogged;
        private static bool? lastMoonActive;
        private static bool? lastBossActive;
        private static object lastMoonSnapshot;
        private static object lastBossSnapshot;

        internal static void Initialize(string path)
        {
            modPath = path;
            cfg = BotSettings.Load(Path.Combine(path, "Config", "DiscordBot.xml"));
            LoadState();
            OptionalIntegrations.Refresh();
            Log.Out("[ZC_DiscordBot] Integraciones: Boss=" + OptionalIntegrations.BossAvailable + " BloodMoon=" + OptionalIntegrations.BloodMoonAvailable);

            if (!cfg.Enabled)
            {
                Log.Warning("[ZC_DiscordBot] Desactivado en Config/DiscordBot.xml.");
                return;
            }

            if (string.IsNullOrWhiteSpace(cfg.Token) || cfg.Token.Contains("PEGAR_") || string.IsNullOrWhiteSpace(cfg.ChannelId) || cfg.ChannelId.Contains("PEGAR_"))
            {
                Log.Warning("[ZC_DiscordBot] Falta configurar BotToken/ChannelId; bot no iniciado.");
                return;
            }

            Log.Out("[ZC_DiscordBot] Config comandos: prefix='" + cfg.Prefix + "' status='" + cfg.Status + "' ranking='" + cfg.Ranking + "' pollSeconds=" + cfg.PollSeconds + ".");
            discord = new DiscordClient(cfg);
            discord.Start();
        }

        internal static void Update()
        {
            if (discord == null)
            {
                return;
            }

            IncomingCommand cmd;
            int count = 0;
            while (count++ < 10 && discord.Commands.TryDequeue(out cmd))
            {
                Handle(cmd);
            }

            DateTime now = DateTime.UtcNow;
            if ((now - lastTickUtc).TotalSeconds < 1)
            {
                return;
            }
            lastTickUtc = now;

            if (!onlineSent && GameManager.Instance != null && GameManager.Instance.World != null)
            {
                onlineSent = true;
                if (cfg.AnnounceOnline)
                {
                    FireSend("✅ **" + cfg.ServerName + " nuevamente ONLINE**");
                }
            }

            OptionalIntegrations.Refresh();
            LogIntegrationChangesOnce();
            UpdateBloodMoonIntegration(now);
            UpdateBossIntegration();
            CheckRestartWarnings();
        }

        private static void LogIntegrationChangesOnce()
        {
            if (integrationsLogged)
            {
                return;
            }
            integrationsLogged = true;
            Log.Out("[ZC_DiscordBot] Estado integraciones runtime: Boss=" + OptionalIntegrations.BossAvailable + " BloodMoon=" + OptionalIntegrations.BloodMoonAvailable);
        }

        private static void Handle(IncomingCommand c)
        {
            string value = c.Content.Substring(cfg.Prefix.Length).Trim().Split(new[] { ' ' }, 2)[0].ToLowerInvariant();
            Log.Out("[ZC_DiscordBot] Procesando comando normalizado: '" + value + "'.");
            if (value == cfg.Status.ToLowerInvariant()) FireSend(StatusText());
            else if (value == cfg.Players.ToLowerInvariant()) FireSend(PlayersText());
            else if (value == cfg.Moon.ToLowerInvariant()) FireSend(MoonText());
            else if (value == cfg.Ranking.ToLowerInvariant()) RepostRanking();
            else if (value == cfg.Boss.ToLowerInvariant()) FireSend(BossText());
            else if (value == cfg.Restart.ToLowerInvariant()) FireSend(RestartText());
            else if (value == cfg.Version.ToLowerInvariant()) FireSend("**Versiones**\nZC_DiscordBot: **1.2.0**\n7 Days to Die: **" + GameVersion() + "**\nBossEvents: **" + (OptionalIntegrations.BossAvailable ? "detectado" : "no detectado") + "**\nBloodMoonRanking: **" + (OptionalIntegrations.BloodMoonAvailable ? "detectado" : "no detectado") + "**");
            else FireSend("Comando no reconocido: `" + c.Content + "`. Usa `" + cfg.Prefix + cfg.Status + "`, `" + cfg.Prefix + cfg.Version + "`, `" + cfg.Prefix + cfg.Ranking + "`.");
        }

        private static string StatusText()
        {
            return "🟢 **" + cfg.ServerName + " ONLINE**\n" + PlayersLine() + "\n☃️ Dia actual: **" + CurrentDay() + "**\n" + MoonLine() + "\n" + BossLine() + "\n" + RestartLine();
        }

        private static string PlayersText()
        {
            World w = GameManager.Instance == null ? null : GameManager.Instance.World;
            List<string> names = new List<string>();
            if (w != null)
            {
                foreach (EntityPlayer p in w.GetPlayers())
                {
                    if (p != null)
                    {
                        names.Add(p.PlayerDisplayName);
                    }
                }
            }
            return "👥 **JUGADORES CONECTADOS (" + names.Count + ")**\n" + (names.Count == 0 ? "No hay jugadores conectados." : string.Join("\n", names.Select(x => "• " + x).ToArray()));
        }

        private static string PlayersLine()
        {
            int n = 0;
            try { n = GameManager.Instance.World.GetPlayers().Count; } catch { }
            return "👥 Jugadores: **" + n + " / " + MaxPlayers() + "**";
        }

        private static void UpdateBloodMoonIntegration(DateTime now)
        {
            object moon = OptionalIntegrations.GetBloodMoonSnapshot();
            bool active = SnapshotBool(moon, "Active");

            if (lastMoonActive.HasValue && active != lastMoonActive.Value)
            {
                if (active)
                {
                    nextRankingUtc = now;
                    rankingMessageId = null;
                    SaveState();
                    FireNewRanking(moon);
                }
                else
                {
                    object finalSnapshot = moon ?? lastMoonSnapshot;
                    string text = FormatRanking(finalSnapshot, true);
                    if (!string.IsNullOrEmpty(rankingMessageId))
                    {
                        FireEdit(rankingMessageId, text);
                    }
                    FireSend(text);
                    SaveState();
                }
            }

            if (active && now >= nextRankingUtc)
            {
                nextRankingUtc = now.AddSeconds(cfg.RankingSeconds);
                UpdateRanking(moon);
            }

            lastMoonActive = active;
            if (moon != null)
            {
                lastMoonSnapshot = moon;
            }
        }

        private static void UpdateBossIntegration()
        {
            object boss = OptionalIntegrations.GetBossSnapshot();
            bool active = SnapshotBool(boss, "Active");

            if (lastBossActive.HasValue && active != lastBossActive.Value)
            {
                if (active)
                {
                    announcedKilledBosses.Clear();
                    FireSend("🚨 **EVENTO BOSS ACTIVO**\nLos Boss estan apareciendo. Finaliza **" + When(SnapshotDate(boss, "EndUtc")) + "**.");
                }
                else
                {
                    object finalSnapshot = boss ?? lastBossSnapshot;
                    List<object> bosses = Bosses(finalSnapshot);
                    int killed = bosses.Count(x => SnapshotBool(x, "Killed"));
                    FireSend("👹 **EVENTO BOSS FINALIZADO**\n" + (killed == 0 ? "❌ **Ningun Boss ha muerto.**" : BossList(finalSnapshot) + "\n\n**" + killed + "/" + bosses.Count + " Boss eliminados**"));
                }
            }

            if (active)
            {
                foreach (object item in Bosses(boss))
                {
                    if (!SnapshotBool(item, "Killed"))
                    {
                        continue;
                    }
                    string key = BossKey(item);
                    if (announcedKilledBosses.Add(key))
                    {
                        string killer = SnapshotString(item, "KillerName");
                        string biome = SnapshotString(item, "BiomeName");
                        FireSend(string.IsNullOrEmpty(killer) ? "🎉 El Boss de **" + biome + "** fue eliminado. **Asesino no identificado.**" : "🎉 **Felicidades " + killer + "**, has matado al Boss del **" + biome + "**.");
                    }
                }
            }

            lastBossActive = active;
            if (boss != null)
            {
                lastBossSnapshot = boss;
            }
        }

        private static string MoonText()
        {
            object m = OptionalIntegrations.GetBloodMoonSnapshot();
            return SnapshotBool(m, "Active") ? "🌕 **LUNA DE SANGRE ACTIVA**\n☃️ Dia: **" + CurrentDay() + "**\nUsa `" + cfg.Prefix + cfg.Ranking + "` para mostrar el ranking." : "🌕 **LUNA DE SANGRE**\n☃️ Dia actual: **" + CurrentDay() + "**\n🌕 Proxima: **" + NextBloodMoonText() + "**";
        }

        private static string MoonLine()
        {
            object m = OptionalIntegrations.GetBloodMoonSnapshot();
            return SnapshotBool(m, "Active") ? "🌕 Luna de Sangre: **ACTIVA**" : "🌕 Proxima Luna de Sangre: **" + NextBloodMoonText() + "**";
        }

        private static string BossText()
        {
            object b = OptionalIntegrations.GetBossSnapshot();
            if (!OptionalIntegrations.BossAvailable)
            {
                return "👹 **EVENTO BOSS**\nIntegracion BossEvents no detectada.";
            }
            if (!SnapshotBool(b, "Active"))
            {
                return "👹 **EVENTO BOSS**\nProximo evento: **" + When(SnapshotDate(b, "NextUtc")) + "**";
            }
            return "👹 **EVENTO BOSS ACTIVO**\nFinaliza: **" + When(SnapshotDate(b, "EndUtc")) + "**\n" + BossList(b);
        }

        private static string BossLine()
        {
            object b = OptionalIntegrations.GetBossSnapshot();
            if (!OptionalIntegrations.BossAvailable)
            {
                return "👹 Evento Boss: **no detectado**";
            }
            return SnapshotBool(b, "Active") ? "👹 Evento Boss: **ACTIVO - termina " + When(SnapshotDate(b, "EndUtc")) + "**" : "👹 Proximo evento Boss: **" + When(SnapshotDate(b, "NextUtc")) + "**";
        }

        private static string BossList(object b)
        {
            List<object> bosses = DistinctBosses(b);
            if (bosses.Count == 0)
            {
                return "Boss generandose...";
            }
            return string.Join("\n", bosses.Select(x => (SnapshotBool(x, "Killed") ? "✅ " : (SnapshotBool(x, "Survived") ? "❌ " : "👹 ")) + SnapshotString(x, "BiomeName") + (SnapshotBool(x, "Killed") ? " - " + (string.IsNullOrEmpty(SnapshotString(x, "KillerName")) ? "eliminado" : SnapshotString(x, "KillerName")) : (SnapshotBool(x, "Survived") ? " - sobrevivio" : " - vivo"))).ToArray());
        }

        private static List<object> DistinctBosses(object b)
        {
            return Bosses(b)
                .GroupBy(x => string.IsNullOrEmpty(SnapshotString(x, "BiomeKey")) ? SnapshotString(x, "BiomeName") : SnapshotString(x, "BiomeKey"), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => SnapshotBool(x, "Killed")).ThenByDescending(x => SnapshotInt(x, "EntityId")).First())
                .ToList();
        }

        private static List<object> Bosses(object b)
        {
            return SnapshotList(b, "Bosses");
        }

        private static string BossKey(object boss)
        {
            int id = SnapshotInt(boss, "EntityId");
            if (id != 0)
            {
                return id.ToString(CultureInfo.InvariantCulture);
            }
            return SnapshotString(boss, "BiomeKey") + ":" + SnapshotString(boss, "BiomeName");
        }

        private static string RestartText()
        {
            return "🔄 **PROXIMO REINICIO**\n" + RestartLine();
        }

        private static string RestartLine()
        {
            DateTime n = NextRestartUtc();
            return n == DateTime.MinValue ? "🔄 Reinicio: **sin horarios configurados**" : "🔄 Proximo reinicio: **" + When(n) + "**";
        }

        private static void RepostRanking()
        {
            object s = OptionalIntegrations.GetBloodMoonSnapshot();
            if (!OptionalIntegrations.BloodMoonAvailable || !SnapshotBool(s, "Active"))
            {
                FireSend(FormatRanking(s, true));
                return;
            }
            if ((DateTime.UtcNow - lastRankingRepostUtc).TotalSeconds < cfg.RepostCooldown)
            {
                FireSend("⏳ El ranking fue republicado recientemente.");
                return;
            }
            lastRankingRepostUtc = DateTime.UtcNow;
            if (cfg.MarkPrevious && !string.IsNullOrEmpty(rankingMessageId))
            {
                FireEdit(rankingMessageId, "📌 Este ranking fue reemplazado. Usa `" + cfg.Prefix + cfg.Ranking + "` para mostrar el ranking activo.");
            }
            FireNewRanking(s);
        }

        private static void FireNewRanking(object s)
        {
            if (discord == null)
            {
                return;
            }
            Task.Run(async () =>
            {
                string id = await discord.Send(FormatRanking(s, false));
                if (!string.IsNullOrEmpty(id))
                {
                    rankingMessageId = id;
                    SaveState();
                }
            });
        }

        private static void UpdateRanking(object s)
        {
            if (string.IsNullOrEmpty(rankingMessageId))
            {
                FireNewRanking(s);
            }
            else
            {
                FireEdit(rankingMessageId, FormatRanking(s, false));
            }
        }

        private static string FormatRanking(object s, bool final)
        {
            List<object> players = SnapshotList(s, "Players");
            List<string> lines = new List<string> { final ? "🏆 **LUNA DE SANGRE FINALIZADA**" : "🌕 **LUNA DE SANGRE — EN CURSO**", "" };
            int i = 0;
            foreach (object p in players)
            {
                i++;
                string medal = i == 1 ? "🥇" : i == 2 ? "🥈" : i == 3 ? "🥉" : i + ".";
                lines.Add(medal + " **" + SnapshotString(p, "Name") + "** - " + SnapshotInt(p, "Kills") + " bajas");
            }
            if (players.Count == 0)
            {
                lines.Add(OptionalIntegrations.BloodMoonAvailable ? "Todavia no hay participantes registrados." : "Integracion BloodMoonRanking no detectada.");
            }
            lines.Add("");
            lines.Add("🧟 Total eliminado: **" + players.Sum(x => SnapshotInt(x, "Kills")) + "**");
            if (final && players.Count > 0)
            {
                lines.Add("👑 Ganador: **" + SnapshotString(players.First(), "Name") + "**");
            }
            else
            {
                lines.Add("🔄 Actualizado: **" + Local(DateTime.UtcNow).ToString("HH:mm:ss") + "**");
            }
            return string.Join("\n", lines.ToArray());
        }

        private static void CheckRestartWarnings()
        {
            DateTime next = NextRestartUtc();
            if (next == DateTime.MinValue) return;
            foreach (int min in cfg.Warnings)
            {
                double left = (next - DateTime.UtcNow).TotalMinutes;
                string key = next.ToString("o") + ":" + min;
                if (left <= min && left > min - 1 && !warningsSent.Contains(key))
                {
                    warningsSent.Add(key);
                    FireSend("⚠️ **REINICIO PROGRAMADO**\nEl servidor se reiniciara en **" + min + " minuto" + (min == 1 ? "" : "s") + "**.");
                }
            }
        }

        private static DateTime NextRestartUtc()
        {
            if (cfg.RestartTimes.Count == 0) return DateTime.MinValue;
            TimeZoneInfo z = Zone();
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, z);
            foreach (TimeSpan t in cfg.RestartTimes.OrderBy(x => x))
            {
                DateTime c = local.Date + t;
                if (c > local) return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(c, DateTimeKind.Unspecified), z);
            }
            DateTime tomorrow = local.Date.AddDays(1) + cfg.RestartTimes.Min();
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(tomorrow, DateTimeKind.Unspecified), z);
        }

        private static TimeZoneInfo Zone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(cfg.TimeZoneId); }
            catch { try { return TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"); } catch { return TimeZoneInfo.Local; } }
        }

        private static DateTime Local(DateTime utc)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone());
        }

        private static string When(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "no disponible";
            TimeSpan d = utc - DateTime.UtcNow;
            string left = d.TotalSeconds <= 0 ? "ahora" : d.TotalHours >= 1 ? ((int)d.TotalHours) + " h " + d.Minutes + " min" : Math.Max(1, (int)Math.Ceiling(d.TotalMinutes)) + " min";
            return Local(utc).ToString("dd/MM HH:mm") + " - " + left;
        }

        private static int CurrentDay()
        {
            try { return GameUtils.WorldTimeToDays(GameManager.Instance.World.worldTime); } catch { return 0; }
        }

        private static string NextBloodMoonText()
        {
            try
            {
                Type e = typeof(GameManager).Assembly.GetType("EnumGameStats");
                object key = Enum.Parse(e, "BloodMoonDay");
                MethodInfo m = typeof(GameStats).GetMethod("GetInt", BindingFlags.Public | BindingFlags.Static);
                int day = (int)m.Invoke(null, new[] { key });
                return day > 0 ? "dia " + day : "no disponible";
            }
            catch { return "consultando al juego"; }
        }

        private static int MaxPlayers()
        {
            try { return GamePrefs.GetInt(EnumGamePrefs.ServerMaxPlayerCount); } catch { return 0; }
        }

        private static string GameVersion()
        {
            try { return Constants.cVersionInformation.LongString; } catch { return "V3.x"; }
        }

        private static void FireSend(string text)
        {
            if (discord != null) Task.Run(() => discord.Send(text));
        }

        private static void FireEdit(string id, string text)
        {
            if (discord != null) Task.Run(() => discord.Edit(id, text));
        }

        private static string StatePath { get { return Path.Combine(modPath, "Data", "DiscordState.txt"); } }

        private static void LoadState()
        {
            try { if (File.Exists(StatePath)) rankingMessageId = File.ReadAllText(StatePath).Trim(); } catch { }
        }

        private static void SaveState()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(StatePath)); File.WriteAllText(StatePath, rankingMessageId ?? ""); } catch { }
        }

        private static DateTime lastUpdateErrorUtc = DateTime.MinValue;

        internal static void LogUpdateError(Exception ex)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastUpdateErrorUtc).TotalSeconds < 30) return;
            lastUpdateErrorUtc = now;
            Log.Warning("[ZC_DiscordBot] Error en Update (ignorado): " + Redact(ex.GetType().Name + " - " + ex.Message));
        }

        internal static string Redact(string text)
        {
            if (cfg != null && !string.IsNullOrEmpty(cfg.Token)) return text.Replace(cfg.Token, "[TOKEN OCULTO]");
            return text;
        }

        private static bool SnapshotBool(object obj, string name)
        {
            object value = OptionalIntegrations.GetProperty(obj, name);
            return value is bool && (bool)value;
        }

        private static int SnapshotInt(object obj, string name)
        {
            object value = OptionalIntegrations.GetProperty(obj, name);
            try { return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static DateTime SnapshotDate(object obj, string name)
        {
            object value = OptionalIntegrations.GetProperty(obj, name);
            return value is DateTime ? (DateTime)value : DateTime.MinValue;
        }

        private static string SnapshotString(object obj, string name)
        {
            object value = OptionalIntegrations.GetProperty(obj, name);
            return value == null ? "" : value.ToString();
        }

        private static List<object> SnapshotList(object obj, string name)
        {
            object value = OptionalIntegrations.GetProperty(obj, name);
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null || value is string) return new List<object>();
            List<object> result = new List<object>();
            foreach (object item in enumerable) if (item != null) result.Add(item);
            return result;
        }
    }

    internal static class OptionalIntegrations
    {
        private static Type bossIntegrationType;
        private static Type bloodMoonIntegrationType;
        private static MethodInfo bossGetSnapshot;
        private static MethodInfo bloodMoonGetSnapshot;
        private static DateTime lastRefreshUtc = DateTime.MinValue;

        internal static bool BossAvailable { get { Refresh(); return bossGetSnapshot != null; } }
        internal static bool BloodMoonAvailable { get { Refresh(); return bloodMoonGetSnapshot != null; } }

        internal static void Refresh()
        {
            if ((DateTime.UtcNow - lastRefreshUtc).TotalSeconds < 10) return;
            lastRefreshUtc = DateTime.UtcNow;
            bossIntegrationType = FindType("ZC.BiomeBossEvents.BossEventIntegration");
            bloodMoonIntegrationType = FindType("ZC.BloodMoonRanking.BloodMoonIntegration");
            bossGetSnapshot = bossIntegrationType == null ? null : bossIntegrationType.GetMethod("GetSnapshot", BindingFlags.Public | BindingFlags.Static);
            bloodMoonGetSnapshot = bloodMoonIntegrationType == null ? null : bloodMoonIntegrationType.GetMethod("GetSnapshot", BindingFlags.Public | BindingFlags.Static);
        }

        internal static object GetBossSnapshot()
        {
            Refresh();
            return InvokeSnapshot(bossGetSnapshot);
        }

        internal static object GetBloodMoonSnapshot()
        {
            Refresh();
            return InvokeSnapshot(bloodMoonGetSnapshot);
        }

        internal static object GetProperty(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                Type t = obj.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanRead) return p.GetValue(obj, null);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static object InvokeSnapshot(MethodInfo method)
        {
            if (method == null) return null;
            try { return method.Invoke(null, null); } catch (Exception ex) { Log.Warning("[ZC_DiscordBot] Integracion snapshot fallo: " + ex.GetType().Name + " - " + ex.Message); return null; }
        }

        private static Type FindType(string fullName)
        {
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null) continue;
                    Type t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }
    }
}
