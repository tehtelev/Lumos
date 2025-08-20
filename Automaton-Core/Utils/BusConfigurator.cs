using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace Automaton.Utils;

[Flags]
[ProtoContract]
public enum BusConfigurator
{
    None = 0b_0000_0000,

    All = 0b_1111_1111,
    Bit1= 0b_0000_0001,
    Bit2 = 0b_0000_0010,
    Bit3 = 0b_0000_0100,
    Bit4 = 0b_0000_1000,
    Bit5 = 0b_0001_0000,
    Bit6 = 0b_0010_0000,
    Bit7 = 0b_0100_0000,
    Bit8 = 0b_1000_0000
}





