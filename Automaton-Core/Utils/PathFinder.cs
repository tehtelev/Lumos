using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Automaton.Utils;



public struct FastPosKeyByteComparer : IEqualityComparer<(FastPosKey key, byte face)>
{
    public bool Equals((FastPosKey key, byte face) x, (FastPosKey key, byte face) y)
    {
        return x.key.Equals(y.key) && x.face == y.face;
    }

    public int GetHashCode((FastPosKey key, byte face) obj)
    {
        unchecked
        {
            int hash = obj.key.GetHashCode();
            hash = (hash << 5) ^ (hash >> 27) ^ obj.face;
            return hash;
        }
    }
}


// Структура для быстрого сравнения и хеширования позиций с учетом измерения (dimension)
public struct FastPosKey : IEquatable<FastPosKey>
{
    public int X, Y, Z, Dim;
    public BlockPos Pos;
    public FastPosKey(int x, int y, int z, int dim, BlockPos pos = null)
    {
        X = x;
        Y = y;
        Z = z;
        Dim = dim;
        Pos = pos;
    }



    // Реализация интерфейса IEquatable для быстрого сравнения
    public bool Equals(FastPosKey other) =>
        X == other.X && Y == other.Y && Z == other.Z && Dim == other.Dim;

    public override bool Equals(object obj) => obj is FastPosKey other && Equals(other);

    // Оптимизированный хеш-код для минимизации коллизий
    public override int GetHashCode()
    {
        unchecked
        {
            // Быстрая версия с битовыми операциями и минимальным количеством операций
            int hash = this.X;
            hash = (hash << 9) ^ (hash >> 23) ^ this.Y;  // Сдвиги и XOR вместо умножения
            hash = (hash << 9) ^ (hash >> 23) ^ this.Z;
            return hash ^ (this.Dim * 269023); // Умножение на простое число для учета измерения
        }
    }
}





// Основной класс для поиска пути в сетях
public class PathFinder
{
    // Маски направлений для соединений (север, восток, юг, запад, верх, низ)
    private static readonly Facing[] faceMasks =
    {
        Facing.NorthAll, Facing.EastAll, Facing.SouthAll,
        Facing.WestAll, Facing.UpAll, Facing.DownAll
    };

    // Переиспользуемые коллекции и буферы для уменьшения нагрузки на GC
    private List<FastPosKey> neighborsFast = new(27); // Соседние позиции
    private List<byte> NeighborsFace = new(27); // Соответствующие направления
    private bool[] NowProcessed = new bool[6]; // Флаги обработки направлений
    private Queue<byte> queue2 = new(); // Очередь для BFS
    private bool[] processFacesBuf = new bool[6]; // Буфер флагов обработки
    private List<BlockFacing> bufForDirections = new(6); // Буфер направлений
    private List<BlockFacing> bufForFaces = new(6); // Буфер граней
    private FastPosKey[] pathBuffer = new FastPosKey[Automaton.maxDistanceForFinding + 1];
    private byte[] faceBuffer = new byte[Automaton.maxDistanceForFinding + 1];


    private List<byte> startBlockFacing = new(); // Стартовые направления
    private List<byte> endBlockFacing = new(); // Конечные направления
    private PriorityQueue<(FastPosKey, byte), int> queue = new(); // Приоритетная очередь для A*
    private Dictionary<(FastPosKey, byte), (FastPosKey, byte)> cameFrom = new(new FastPosKeyByteComparer()); // Для восстановления пути
    private Dictionary<FastPosKey, bool[]> processedFaces = new(); // Обработанные направления для позиций
    private Dictionary<(FastPosKey, byte), byte> facingFrom = new(new FastPosKeyByteComparer()); // Направления прихода
    public Dictionary<(FastPosKey, byte), bool[]> nowProcessedFaces = new(new FastPosKeyByteComparer()); // Текущие обработанные направления
    private HashSet<BlockPos> networkPositions = new(); // Позиции в сети
    private List<FastPosKey> buf1 = new(); // Временный буфер
    private List<byte> buf2 = new(); // Временный буфер
    private bool[]? buf3; // Временный буфер
    private bool[]? buf4; // Временный буфер

    // lookupPos для поиска в parts без создания новых объектов
    private BlockPos lookupPos = new BlockPos(0, 0, 0, 0);
    // defaultKey для сравнений с default
    private static FastPosKey defaultKey = new FastPosKey(0, 0, 0, 0);

    // Получение NetworkPart по FastPosKey с переиспользованием lookupPos
    private bool TryGetPart(ConcurrentDictionary<BlockPos, NetworkPart> parts, FastPosKey key, out NetworkPart part)
    {
        lookupPos.X = key.X;
        lookupPos.Y = key.Y;
        lookupPos.Z = key.Z;
        lookupPos.dimension = key.Dim;
        return parts.TryGetValue(lookupPos, out part);
    }


    public void Clear()
    {
        nowProcessedFaces.Clear();
        processedFaces.Clear(); // Очистка состояния
    }

    // Эвристика для A* (манхэттенское расстояние)
    private static int Heuristic(BlockPos a, BlockPos b)
        => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z);

    /// <summary>
    /// Основной поиск пути
    /// </summary>
    public (BlockPos[], byte[], bool[][], Facing[]) FindShortestPath(
        BlockPos start, BlockPos end, Network network, ConcurrentDictionary<BlockPos, NetworkPart> parts)
    {
        // Инициализация коллекций
        startBlockFacing.Clear();
        endBlockFacing.Clear();
        queue.Clear();
        cameFrom.Clear();
        facingFrom.Clear();
        buf1.Clear();
        buf2.Clear();
        buf3 = Array.Empty<bool>();
        buf4 = Array.Empty<bool>();

        networkPositions = network.PartPositions;

        // стартовые и конечные значения
        var startKey = new FastPosKey(start.X, start.Y, start.Z, start.dimension, start);
        var endKey = new FastPosKey(end.X, end.Y, end.Z, end.dimension, end);

        // Проверка на валидность старта и конца
        if (!networkPositions.Contains(start) ||
            !networkPositions.Contains(end) ||
            Heuristic(start, end) >= Automaton.maxDistanceForFinding ||
            start.Equals(end))
            return (null!, null!, null!, null!);

        // Заполнение стартовых и конечных направлений
        foreach (var face in FacingHelper.Faces(parts[start].Connection))
            startBlockFacing.Add((byte)face.Index);
        foreach (var face in FacingHelper.Faces(parts[end].Connection))
            endBlockFacing.Add((byte)face.Index);

        // Добавление стартовых точек в очередь
        foreach (var sFace in startBlockFacing)
        {
            queue.Enqueue((startKey, sFace), 0);
            cameFrom[(startKey, sFace)] = (defaultKey, 0);
            facingFrom[(startKey, sFace)] = sFace;

            if (!nowProcessedFaces.TryGetValue((startKey, sFace), out var val))
            {
                var buffer = new bool[6];
                buffer[sFace] = true;
                nowProcessedFaces.Add((startKey, sFace), buffer);
            }
            else
            {
                Array.Fill(val, false);
                val[sFace] = true;
            }
        }

        // Инициализация processedFaces для всех позиций сети
        foreach (var pos in networkPositions)
        {
            if (!processedFaces.TryGetValue(new FastPosKey(pos.X, pos.Y, pos.Z, pos.dimension), out var val))
                processedFaces.Add(new FastPosKey(pos.X, pos.Y, pos.Z, pos.dimension), new bool[6]);
            else Array.Fill(val, false);
        }

        FastPosKey currentKey = defaultKey;
        byte currentFace = 0;

        // Основной цикл A*
        while (queue.Count > 0)
        {
            (currentKey, currentFace) = queue.Dequeue();
            if (currentKey.Equals(endKey)) // Путь найден
                break;

            // Получение соседей
            (buf1, buf2, buf3, buf4) = GetNeighbors(currentKey, processedFaces[currentKey], facingFrom[(currentKey, currentFace)], network, parts);

            processedFaces[currentKey] = buf4; // Обновление обработанных направлений

            int i = 0;
            foreach (var neighbor in buf1)
            {
                var state = (neighbor, buf2[i]);
                int priority = Math.Abs(neighbor.X - end.X) +
                               Math.Abs(neighbor.Y - end.Y) +
                               Math.Abs(neighbor.Z - end.Z);

                // Добавление в очередь, если соответствует условиям
                if (priority < Automaton.maxDistanceForFinding &&
                    !cameFrom.ContainsKey(state) &&
                    !processedFaces[neighbor][buf2[i]])
                {
                    queue.Enqueue(state, priority);
                    cameFrom[state] = (currentKey, facingFrom[(currentKey, currentFace)]);
                    facingFrom[state] = buf2[i];

                    // Копирование массива флагов
                    if (!nowProcessedFaces.TryGetValue(state, out var val))
                    {
                        nowProcessedFaces.Add(state, buf3);
                    }
                    else
                    {
                        val[0] = (buf3[0]) ? true : false;
                        val[1] = (buf3[1]) ? true : false;
                        val[2] = (buf3[2]) ? true : false;
                        val[3] = (buf3[3]) ? true : false;
                        val[4] = (buf3[4]) ? true : false;
                        val[5] = (buf3[5]) ? true : false;
                    }
                }
                i++;
            }
        }

        // Восстановление пути
        var (fastPath, faces, pathLength) = ReconstructFastPath(startKey, endKey, endBlockFacing, cameFrom);
        if (fastPath == null)
            return (null!, null!, null!, null!);

        // Конвертация FastPosKey[] в BlockPos[] для пути
        var path = new BlockPos[pathLength];
        for (int i = 0; i < pathLength; i++)
        {
            var key = fastPath[i];

            if (key.Pos != null)
                path[i] = key.Pos; // Используем существующий BlockPos
            else
                return (null!, null!, null!, null!);
        }

        // Построение дополнительных данных о пути
        int len = path.Length;
        Facing[] nowProcessingFaces = new Facing[len];
        bool[][] nowProcessedFacesList = new bool[len][];
        byte[] facingFromList = new byte[len];

        facingFromList[0] = facingFrom[(fastPath[0], faces[0])];

        for (int i = 1; i < len; i++)
        {
            facingFromList[i] = facingFrom[(fastPath[i], faces[i])];
            var npf = nowProcessedFaces[(fastPath[i], faces[i])];
            nowProcessedFacesList[i - 1] = npf;

            // Вычисление активных направлений
            var facing = parts[path[i - 1]].Connection &
                ((npf[0] ? Facing.NorthAll : Facing.None) |
                 (npf[1] ? Facing.EastAll : Facing.None) |
                 (npf[2] ? Facing.SouthAll : Facing.None) |
                 (npf[3] ? Facing.WestAll : Facing.None) |
                 (npf[4] ? Facing.UpAll : Facing.None) |
                 (npf[5] ? Facing.DownAll : Facing.None));

            nowProcessingFaces[i - 1] = facing;
        }

        // Обработка последней позиции
        var lastNpf = new bool[6];
        lastNpf[endBlockFacing[0]] = true;
        nowProcessedFacesList[len - 1] = lastNpf;
        nowProcessingFaces[len - 1] = parts[path[len - 1]].Connection &
            ((lastNpf[0] ? Facing.NorthAll : Facing.None) |
             (lastNpf[1] ? Facing.EastAll : Facing.None) |
             (lastNpf[2] ? Facing.SouthAll : Facing.None) |
             (lastNpf[3] ? Facing.WestAll : Facing.None) |
             (lastNpf[4] ? Facing.UpAll : Facing.None) |
             (lastNpf[5] ? Facing.DownAll : Facing.None));

        return (path, facingFromList, nowProcessedFacesList, nowProcessingFaces);
    }



    // Восстановление пути от конца к началу
    private (FastPosKey[]?, byte[]?, int) ReconstructFastPath(
        FastPosKey start, FastPosKey end, List<byte> endFacing,
        Dictionary<(FastPosKey, byte), (FastPosKey, byte)> cameFrom)
    {
        int length = 0;
        var current = (end, endFacing[0]);

        // Подсчет длины пути
        while (!current.Item1.Equals(defaultKey))
        {
            length++;
            if (current.Item1.Equals(end))
            {
                bool valid = false;
                foreach (var eFace in endFacing)
                {
                    current = (end, eFace);
                    if (cameFrom.TryGetValue(current, out current)) { valid = true; break; }
                }
                if (!valid) return (null, null, 0);
            }
            else if (!cameFrom.TryGetValue(current, out current))
                return (null, null, 0);
        }

        // Построение массива пути
        var pathArray = pathBuffer;
        var faceArray = faceBuffer;
        current = (end, endFacing[0]);

        for (int i = length - 1; i >= 0; i--)
        {
            pathArray[i] = current.Item1;
            faceArray[i] = current.Item2;
            if (!cameFrom.TryGetValue(current, out current))
                break;
        }

        return pathArray[0].Equals(start) ? (pathArray, faceArray, length) : (null, null, 0);
    }




    // Получение соседних позиций с учетом направлений и соединений
    private (List<FastPosKey>, List<byte>, bool[], bool[]) GetNeighbors(
    FastPosKey pos, bool[] processFaces, byte startFace,
    Network network, ConcurrentDictionary<BlockPos, NetworkPart> parts)
    {
        neighborsFast.Clear();
        NeighborsFace.Clear();
        NowProcessed.Fill(false);
        queue2.Clear();
        processFacesBuf.Fill(false);

        if (!TryGetPart(parts, pos, out var part))
            return (neighborsFast, NeighborsFace, NowProcessed, processFaces);

        var Connections = part.Connection;
        Facing hereConnections = Facing.None;

        // Определение доступных соединений
        for (byte i = 0; i < 6; i++)
            if (part.Networks[i] == network && !processFaces[i])
                hereConnections |= Connections & faceMasks[i];

        // BFS по направлениям
        queue2.Enqueue(startFace);
        processFaces.CopyTo(processFacesBuf, 0);
        processFacesBuf[startFace] = true;

        while (queue2.Count > 0)
        {
            int faceIndex = queue2.Dequeue();
            var face = FacingHelper.BlockFacingFromIndex(faceIndex);
            var mask = FacingHelper.FromFace(face);
            var connections = hereConnections & mask;

            FacingHelper.FillDirections(connections, bufForDirections);
            foreach (var dir in bufForDirections)
            {
                byte idx = (byte)dir.Index;
                if (!processFacesBuf[idx] &&
                    (hereConnections & FacingHelper.From(dir, face)) != 0)
                {
                    processFacesBuf[idx] = true;
                    queue2.Enqueue(idx);
                }
            }
        }

        // Формирование маски валидных направлений
        Facing validMask = Facing.None;
        for (byte i = 0; i < 6; i++)
            if (processFacesBuf[i])
                validMask |= FacingHelper.FromFace(FacingHelper.BlockFacingFromIndex(i));
        hereConnections &= validMask;

        int px = part.Position.X, py = part.Position.Y, pz = part.Position.Z, dim = part.Position.dimension;
        FacingHelper.FillDirections(hereConnections, bufForDirections);

        foreach (var dir in bufForDirections)
        {
            // ищем соседей по граням
            var dv = dir.Normali;
            int nx = px + dv.X, ny = py + dv.Y, nz = pz + dv.Z;
            var neighborKey = new FastPosKey(nx, ny, nz, dim);

            if (TryGetPart(parts, neighborKey, out var neighborPart))
            {
                FacingHelper.FillFaces(hereConnections & FacingHelper.FromDirection(dir), bufForFaces);
                foreach (var face in bufForFaces)
                {
                    var opp = dir.Opposite;
                    if ((neighborPart.Connection & FacingHelper.From(face, opp)) != 0)
                    {
                        neighborsFast.Add(new FastPosKey(nx, ny, nz, dim, neighborPart.Position));
                        NeighborsFace.Add((byte)face.Index);
                        NowProcessed[face.Index] = true;
                        processFaces[face.Index] = true;
                    }

                    if ((neighborPart.Connection & FacingHelper.From(opp, face)) != 0)
                    {
                        neighborsFast.Add(new FastPosKey(nx, ny, nz, dim, neighborPart.Position));
                        NeighborsFace.Add((byte)opp.Index);
                        NowProcessed[face.Index] = true;
                        processFaces[face.Index] = true;
                    }
                }
            }

            // ищем соседей по ребрам
            FacingHelper.FillFaces(hereConnections & FacingHelper.FromDirection(dir), bufForFaces);
            foreach (var face in bufForFaces)
            {
                dv = dir.Normali;
                var fv = face.Normali;
                nx = px + dv.X + fv.X;
                ny = py + dv.Y + fv.Y;
                nz = pz + dv.Z + fv.Z;

                neighborKey = new FastPosKey(nx, ny, nz, dim);

                if (TryGetPart(parts, neighborKey, out neighborPart))
                {
                    var oppDir = dir.Opposite;
                    var oppFace = face.Opposite;

                    if ((neighborPart.Connection & FacingHelper.From(oppDir, oppFace)) != 0)
                    {
                        neighborsFast.Add(new FastPosKey(nx, ny, nz, dim, neighborPart.Position));
                        NeighborsFace.Add((byte)oppDir.Index);
                        NowProcessed[face.Index] = true;
                        processFaces[face.Index] = true;
                    }

                    if ((neighborPart.Connection & FacingHelper.From(oppFace, oppDir)) != 0)
                    {
                        neighborsFast.Add(new FastPosKey(nx, ny, nz, dim, neighborPart.Position));
                        NeighborsFace.Add((byte)oppFace.Index);
                        NowProcessed[face.Index] = true;
                        processFaces[face.Index] = true;
                    }
                }
            }

            // ищем соседей по перпендикулярной грани
            FacingHelper.FillFaces(hereConnections & FacingHelper.FromDirection(dir), bufForFaces);
            foreach (var face in bufForFaces)
            {
                var fv = face.Normali;
                nx = px + fv.X;
                ny = py + fv.Y;
                nz = pz + fv.Z;

                neighborKey = new FastPosKey(nx, ny, nz, dim);

                if (TryGetPart(parts, neighborKey, out neighborPart))
                {
                    var oppFace = face.Opposite;

                    if ((neighborPart.Connection & FacingHelper.From(dir, oppFace)) != 0)
                    {
                        neighborsFast.Add(new FastPosKey(nx, ny, nz, dim, neighborPart.Position));
                        NeighborsFace.Add((byte)dir.Index);
                        NowProcessed[face.Index] = true;
                        processFaces[face.Index] = true;
                    }

                    if ((neighborPart.Connection & FacingHelper.From(oppFace, dir)) != 0)
                    {
                        neighborsFast.Add(new FastPosKey(nx, ny, nz, dim, neighborPart.Position));
                        NeighborsFace.Add((byte)oppFace.Index);
                        NowProcessed[face.Index] = true;
                        processFaces[face.Index] = true;
                    }
                }
            }
        }

        return (neighborsFast, NeighborsFace, NowProcessed, processFaces);
    }
}
