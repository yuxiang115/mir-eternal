using System;
using System.IO;
using System.Text;

namespace GameServer.Bots
{
    /// <summary>
    /// 每个 bot 一个行为流水文件(Log/Bots/&lt;名字&gt;.jsonl),一行一条 JSON:
    ///   {"t":"HH:mm:ss","cat":"think|act|say|event|memory|commit|cache","data":"..."}
    /// 用于收集实验数据:决策频率、工具分布、说话内容、知识/承诺演变。
    /// 只记决策级行为(思考/工具/说话/里程碑事件),不记反射层心跳(攻击/移动步)。
    /// </summary>
    public static class BotLogger
    {
        private static readonly object WriteLock = new object();

        private static string Dir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Log", "Bots"); }
        }

        public static void Log(string botName, string cat, string data)
        {
            try
            {
                lock (WriteLock)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(
                        Path.Combine(Dir, SafeName(botName) + ".jsonl"),
                        "{\"t\":\"" + DateTime.Now.ToString("HH:mm:ss")
                        + "\",\"cat\":\"" + Escape(cat)
                        + "\",\"data\":\"" + Escape(data ?? "") + "\"}\n",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败绝不能影响游戏主循环
            }
        }

        private static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string Escape(string text)
        {
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", "\\n");
        }
    }
}
