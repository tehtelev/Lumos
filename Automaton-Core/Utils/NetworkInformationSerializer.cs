using System.IO;
using System.Text;

namespace Automaton.Utils
{
    public static class NetworkInformationSerializer
    {
        public static byte[] Serialize(NetworkInformation info)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms, Encoding.UTF8))
            {
                writer.Write(info.Consumption);
                writer.Write(info.Capacity);
                writer.Write(info.MaxCapacity);
                writer.Write(info.Production);
                writer.Write(info.Request);
                writer.Write((int)info.Facing);
                writer.Write(info.NumberOfAccumulators);
                writer.Write(info.NumberOfBlocks);
                writer.Write(info.NumberOfConsumers);
                writer.Write(info.NumberOfProducers);
                writer.Write(info.NumberOfTransformators);

                // --- сериализация AParamsInNetwork ---
                var eparam = info.AParamsInNetwork;
                writer.Write((int)eparam.configurator);

                writer.Write(eparam.signal.Length);   // длина массива
                foreach (bool b in eparam.signal)
                    writer.Write(b);

                writer.Write(info.current);

                return ms.ToArray();
            }
        }

        public static NetworkInformation Deserialize(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms, Encoding.UTF8))
            {
                var info = new NetworkInformation();
                info.Consumption = reader.ReadSingle();
                info.Capacity = reader.ReadSingle();
                info.MaxCapacity = reader.ReadSingle();
                info.Production = reader.ReadSingle();
                info.Request = reader.ReadSingle();
                info.Facing = (Facing)reader.ReadInt32();
                info.NumberOfAccumulators = reader.ReadInt32();
                info.NumberOfBlocks = reader.ReadInt32();
                info.NumberOfConsumers = reader.ReadInt32();
                info.NumberOfProducers = reader.ReadInt32();
                info.NumberOfTransformators = reader.ReadInt32();

                // --- десериализация AParamsInNetwork ---
                var eparam = new AParams();
                eparam.configurator = (BusConfigurator) reader.ReadInt32();

                int signalLen = reader.ReadInt32();
                bool[] signal = new bool[signalLen];
                for (int j = 0; j < signalLen; j++)
                    signal[j] = reader.ReadBoolean();
                eparam.signal = signal;

                info.AParamsInNetwork = eparam;

                info.current = reader.ReadSingle();

                return info;
            }
        }
    }
}
