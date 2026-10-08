using GameServer.Networking;

namespace GameServer.Bots
{
    /// <summary>
    /// 机器人专用的虚拟连接:不持有真实 Socket,所有下行封包直接丢弃。
    /// 机器人由服务端内部直接驱动 PlayerObject,不经过网络协议。
    /// 此类实例绝不加入 NetworkServiceGateway.Connections,因此 Process() 永远不会被调用。
    /// </summary>
    public sealed class BotConnection : SConnection
    {
        public BotConnection()
        {
        }

        public override void SendPacket(GamePacket packet)
        {
        }

        public override void SendRaw(ushort type, ushort length, byte[] data, bool encoded = true)
        {
        }
    }
}
