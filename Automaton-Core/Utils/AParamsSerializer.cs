using System.IO;
using System.Text;

namespace Automaton.Utils
{
    public static class AParamsSerializer
    {
        public static byte[] Serialize(AParams[] eparamsArray)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms, Encoding.UTF8))
            {
                // Записываем длину массива
                writer.Write(eparamsArray.Length);
                foreach (var eparam in eparamsArray)
                {
                    writer.Write((int)eparam.configurator);        // строка с префиксом длины

                    // --- сериализация signal ---
                    /*
                    writer.Write(eparam.signal.Length);   // длина массива
                    foreach (bool b in eparam.signal)
                        writer.Write(b);                  // каждый bool как 1 байт
                    */
                }
                return ms.ToArray();
            }
        }

        public static AParams[] Deserialize(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms, Encoding.UTF8))
            {
                // Читаем длину массива
                int length = reader.ReadInt32();
                AParams[] eparamsArray = new AParams[length];
                for (int i = 0; i < length; i++)
                {
                    BusConfigurator configurator = (BusConfigurator)reader.ReadInt32();

                    /*
                    // --- десериализация signal ---
                    int signalLen = reader.ReadInt32();
                    bool[] signal = new bool[signalLen];
                    for (int j = 0; j < signalLen; j++)
                        signal[j] = reader.ReadBoolean();
                    */

                    eparamsArray[i] = new AParams
                    {
                        configurator = configurator,
                        //signal = signal
                    };
                }
                return eparamsArray;
            }
        }
    }

}