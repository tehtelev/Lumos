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
                writer.Write((int)eparam.signal);   
                


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
                eparam.signal = (BusConfigurator)reader.ReadInt32();

                info.AParamsInNetwork=eparam;

                return info;
            }
        }
    }
}
