using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ZC.DiscordBot
{
    internal sealed class BotSettings
    {
        internal bool Enabled; internal string Token=""; internal string ChannelId=""; internal int PollSeconds=2;
        internal string Prefix="!", Status="estado", Players="jugadores", Moon="luna", Ranking="ranking", Boss="boss", Restart="reinicio", Version="version";
        internal string TimeZoneId="America/Argentina/Buenos_Aires", ServerName="ZONA CERO";
        internal bool AnnounceOnline=true, MarkPrevious=true, Verbose=false; internal int RankingSeconds=60, RepostCooldown=60;
        internal readonly List<TimeSpan> RestartTimes=new List<TimeSpan>(); internal readonly List<int> Warnings=new List<int>();
        internal static BotSettings Load(string path)
        {
            BotSettings s=new BotSettings(); XDocument d=XDocument.Load(path); XElement r=d.Root;
            XElement x=r.Element("Discord"); s.Enabled=B(x,"enabled",false); s.Token=S(x,"botToken",""); s.ChannelId=S(x,"channelId",""); s.PollSeconds=Math.Max(2,I(x,"pollSeconds",2));
            x=r.Element("Commands"); s.Prefix=S(x,"prefix","!"); s.Status=S(x,"status","estado"); s.Players=S(x,"players","jugadores"); s.Moon=S(x,"moon","luna"); s.Ranking=S(x,"ranking","ranking"); s.Boss=S(x,"boss","boss"); s.Restart=S(x,"restart","reinicio"); s.Version=S(x,"version","version");
            x=r.Element("Restart"); s.TimeZoneId=S(x,"timeZoneId",s.TimeZoneId); s.AnnounceOnline=B(x,"announceOnline",true);
            if(x!=null) foreach(XElement n in x.Elements("Time")){TimeSpan t;if(TimeSpan.TryParseExact(S(n,"value",""),@"hh\:mm",CultureInfo.InvariantCulture,out t))s.RestartTimes.Add(t);}
            if(x!=null) foreach(XElement n in x.Elements("Warning"))s.Warnings.Add(Math.Max(1,I(n,"minutes",1)));
            x=r.Element("BloodMoon"); s.RankingSeconds=Math.Max(15,I(x,"updateSeconds",60)); s.RepostCooldown=Math.Max(10,I(x,"rankingRepostCooldownSeconds",60)); s.MarkPrevious=B(x,"markPreviousMessage",true);
            x=r.Element("Messages"); s.ServerName=S(x,"serverName",s.ServerName); x=r.Element("Logging"); s.Verbose=B(x,"verbose",false);
            return s;
        }
        private static string S(XElement e,string n,string v){XAttribute a=e==null?null:e.Attribute(n);return a==null?v:a.Value;}
        private static int I(XElement e,string n,int v){int x;return int.TryParse(S(e,n,""),out x)?x:v;}
        private static bool B(XElement e,string n,bool v){bool x;return bool.TryParse(S(e,n,""),out x)?x:v;}
    }
}
