using Automaton.Content.Block.ACable;
using Automaton.Utils;
using Cairo.Freetype;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Automaton.Content.Block.ABus
{
    public class BlockABus : BlockEBase
    {
        private readonly static ConcurrentDictionary<CacheDataKey, Dictionary<Facing, Cuboidf[]>> CollisionBoxesCache = new();

        public readonly static ConcurrentDictionary<CacheDataKey, Dictionary<Facing, Cuboidf[]>> SelectionBoxesCache = new();

        public readonly static Dictionary<CacheDataKey, MeshData> MeshDataCache = new();

        // Таблица всех поворотов
        private static readonly Dictionary<BlockFacing, (float x, float y, float z)> RotationMap = new()
        {
            // North
            { BlockFacing.NORTH, (90, 0, 0) },
            // East
            { BlockFacing.EAST, (90, 0, 90) },
            // South
            { BlockFacing.SOUTH,   (270, 180, 0) },
            // West
            { BlockFacing.WEST,    (90, 0, 270) },
            // Up
            { BlockFacing.UP,  (180, 0, 0) },
            // Down
            { BlockFacing.DOWN, (0, 0, 0) }
        };


        


        public float res;                       //удельное сопротивление из ассета
        public float maxCurrent;                //максимальный ток из ассета
        public float crosssectional;            //площадь сечения из ассета
        public string material = "";              //материал из ассета




        public static Dictionary<int, string> types = new()
        {
            { 0, "block" },
            { 1, "n" },
            { 2, "ne" },
            { 3, "ns" },
            { 4, "nes" },
            { 5, "nesw" }
        };

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
        }

        public override void OnUnloaded(ICoreAPI api)
        {
            base.OnUnloaded(api);
            BlockABus.CollisionBoxesCache.Clear();
            BlockABus.SelectionBoxesCache.Clear();
            BlockABus.MeshDataCache.Clear();
        }

        public override bool IsReplacableBy(Vintagestory.API.Common.Block block)
        {
            return base.IsReplacableBy(block) || block is BlockABus;
        }


        /// <summary>
        /// Ставим кабель
        /// </summary>
        /// <param name="world"></param>
        /// <param name="byPlayer"></param>
        /// <param name="blockSelection"></param>
        /// <param name="byItemStack"></param>
        /// <returns></returns>
        public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSelection, ItemStack byItemStack)
        {
            var selection = new Selection(blockSelection);
            var facing = FacingHelper.From(selection.Face, selection.Direction);
            var faceIndex = FacingHelper.Faces(facing).First().Index;
            var currentGameMode = byPlayer.WorldData.CurrentGameMode;

            // Если размещаем кабель в блоке без кабелей
            if (world.BlockAccessor.GetBlockEntity(blockSelection.Position) is not BlockEntityABus entity)
            {
                if (!HasSolidNeighbor(world, blockSelection.Position, faceIndex))
                    return false;

                // если установка все же успешна
                if (!base.DoPlaceBlock(world, byPlayer, blockSelection, byItemStack))
                    return false;

                // В теории такого не должно произойти
                if (world.BlockAccessor.GetBlockEntity(blockSelection.Position) is not BlockEntityABus placedCable)
                    return false;

                // обновляем текущий блок с кабелем 
                var material = MyMiniLib.GetAttributeString(byItemStack.Block, "material", "");  // определяем материал
                material = "12345678";

                var newAparams = new AParams(material);

                placedCable.Connection = facing;       //сообщаем направление
                placedCable.Aparams = (newAparams, faceIndex);

                placedCable.AllAparams![faceIndex] = newAparams;
                //markdirty тут строго не нужен!

                return true;
            }

            // обновляем текущий блок с кабелем 
            
            if ((entity.Connection & facing) == 0)  //мы навелись уже на существующий кабель?
            {
                //проверка на сплошную соседнюю грань
                if (!HasSolidNeighbor(world, blockSelection.Position, faceIndex))
                    return false;


                //подгружаем некоторые параметры из ассета
                var material = MyMiniLib.GetAttributeString(byItemStack.Block, "material", "");  //определяем материал
                material = "12345678";

                var emptyMaterial = entity.AllAparams![faceIndex].material; //а было ли что-то
                //линий 0? Значит грань была пустая
                if (emptyMaterial == null || emptyMaterial == "")
                {
                    var newAparams = new AParams(material);
                    entity.Aparams = (newAparams, faceIndex);

                    entity.AllAparams[faceIndex] = newAparams;
                }
                else   //линий не 0, значит уже что-то там есть на грани
                {
                    //какой блок сейчас здесь находится
                    var indexM2 = entity.AllAparams[faceIndex].material;          //индекс материала этой грани


                    var block = new GetAsset().BusAsset(api, entity.Block, indexM2, 1); // берем ассет блока кабеля

                    //проверяем сколько у игрока проводов в руке и совпадают ли они с теми что есть
                    if (!CanAddCableToFace(block.Code, currentGameMode, byItemStack, 1))
                        return false;

                    //if (currentGameMode == EnumGameMode.Creative) // чтобы в креативе не уменьшало стак
                    //    byItemStack.StackSize += 1;

                    var newEparams = new AParams(material);
                    entity.Aparams = (newEparams, faceIndex);

                    entity.AllAparams[faceIndex] = newEparams;
                }

                entity.Connection |= facing;
                entity.MarkDirty(true);
            }
            else
            {
                return false; // уже есть кабель в этом направлении
            }

            return true;
        }

        private bool HasSolidNeighbor(IWorldAccessor world, BlockPos pos, int faceIndex)
        {
            var neighborPos = pos.Copy();
            int checkFace;

            switch (faceIndex)
            {
                case 0: neighborPos.Z--; checkFace = 2; break;
                case 1: neighborPos.X++; checkFace = 3; break;
                case 2: neighborPos.Z++; checkFace = 0; break;
                case 3: neighborPos.X--; checkFace = 1; break;
                case 4: neighborPos.Y++; checkFace = 5; break;
                case 5: neighborPos.Y--; checkFace = 4; break;
                default: return false;
            }

            var neighborBlock = world.BlockAccessor.GetBlock(neighborPos);
            return neighborBlock != null && neighborBlock.SideIsSolid(neighborPos, checkFace);
        }

        private bool CanAddCableToFace(AssetLocation requiredCable, EnumGameMode gameMode, ItemStack itemStack, int requiredCount)
        {
            if (api is not ICoreClientAPI clientApi)
                return true;


            if (!itemStack.Block.Code.ToString().Contains(requiredCable))
            {
                clientApi.TriggerIngameError(this, "cable", "Кабеля должны быть того же типа.");
                return false;
            }

            if (gameMode != EnumGameMode.Creative && itemStack.StackSize < requiredCount)
            {
                clientApi.TriggerIngameError(this, "cable", "Недостаточно кабелей для размещения.");
                return false;
            }

            return true;
        }

        public override void OnBlockBroken(IWorldAccessor world, BlockPos position, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            if (this.api is ICoreClientAPI)
                return;

            if (world.BlockAccessor.GetBlockEntity(position) is not BlockEntityABus entity)
            {
                base.OnBlockBroken(world, position, byPlayer, dropQuantityMultiplier);
                return;
            }

            if (byPlayer is not { CurrentBlockSelection: { } blockSelection })
            {
                base.OnBlockBroken(world, position, byPlayer, dropQuantityMultiplier);
                return;
            }

            var key = CacheDataKey.FromEntity(entity);
            var hitPosition = blockSelection.HitPosition;

            var sf = new SelectionFacingCable();
            var selectedFacing = sf.SelectionFacing(key, hitPosition, entity); // выделяем направление для слома под курсором

            //определяем какой выключатель ломать
            var faceSelect = Facing.None;


            if (selectedFacing != Facing.None)
            {
                faceSelect = FacingHelper.FromFace(FacingHelper.Faces(selectedFacing).First());
            }



            // здесь уже ломаем кабеля
            var connection = entity.Connection & ~selectedFacing; // отнимает выбранные соединения
            if (connection == Facing.None)
            {
                base.OnBlockBroken(world, position, byPlayer, dropQuantityMultiplier);
                return;
            }

            var stackSize = FacingHelper.Count(selectedFacing); // соединений выделено
            if (stackSize <= 0)
            {
                base.OnBlockBroken(world, position, byPlayer, dropQuantityMultiplier);
                return;
            }

            entity.Connection = connection;
            entity.MarkDirty(true);

            //перебираем все грани выделенных кабелей
            foreach (var face in FacingHelper.Faces(selectedFacing))
            {

                var material = entity.AllAparams[face.Index].material; //индекс материала этой грани
                material = "12345678";

                // берем направления только в этой грани
                connection = selectedFacing & FacingHelper.FromFace(face);

                //если грань осталась пустая
                if ((entity.Connection & FacingHelper.FromFace(face)) == 0)
                    entity.AllAparams[face.Index] = new();

                //сколько на этой грани проводов выронить
                stackSize = FacingHelper.Count(connection);

                ItemStack itemStack = null!;

                // берем ассет блока кабеля
                var block = new GetAsset().BusAsset(api, entity.Block, material, 1);
                itemStack = new(block, stackSize);


                world.SpawnItemEntity(itemStack, position.ToVec3d());
            }
        }


        /// <summary>
        /// Роняем все соединения этого блока?
        /// </summary>
        /// <param name="world"></param>
        /// <param name="position"></param>
        /// <param name="byPlayer"></param>
        /// <param name="dropQuantityMultiplier"></param>
        /// <returns></returns>
        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos position, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            if (world.BlockAccessor.GetBlockEntity(position) is not BlockEntityABus entity)
                return base.GetDrops(world, position, byPlayer, dropQuantityMultiplier);

            var itemStacks = new ItemStack[] { };

            var connection = entity.Connection;

            foreach (var face in FacingHelper.Faces(entity.Connection))         //перебираем все грани выделенных кабелей
            {

                var material = entity.AllAparams[face.Index].material;          //индекс материала этой грани
                material = "12345678";

                connection = entity.Connection & FacingHelper.FromFace(face);                   //берем направления только в этой грани

                if ((entity.Connection & FacingHelper.FromFace(face)) == 0) //если грань осталась пустая
                    entity.AllAparams[face.Index] = new();

                var stackSize = FacingHelper.Count(connection);          //сколько на этой грани проводов выронить

                var itemStack = default(ItemStack?);

                //берем ассет блока кабеля
                var block = new GetAsset().BusAsset(api, entity.Block, material, 1);
                itemStack = new(block, stackSize);


                itemStacks = itemStacks.AddToArray(itemStack);
            }

            return itemStacks;

        }

        /// <summary>
        /// Обновился соседний блок
        /// </summary>
        /// <param name="world"></param>
        /// <param name="pos"></param>
        /// <param name="neibpos"></param>
        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);

            if (world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityABus entity)
                return;

            var blockFacing = BlockFacing.FromVector(neibpos.X - pos.X, neibpos.Y - pos.Y, neibpos.Z - pos.Z);
            var selectedFacing = FacingHelper.FromFace(blockFacing);

            var delayReturn = false;
            if ((entity.Connection & ~selectedFacing) == Facing.None)
            {
                world.BlockAccessor.BreakBlock(pos, null);

                delayReturn = true;
                //return;
            }



            if (delayReturn)
                return;

            //ломаем провода
            var selectedConnection = entity.Connection & selectedFacing;
            if (selectedConnection == Facing.None)
                return;

            //соединений выделено
            var connectionStackSize = FacingHelper.Count(selectedConnection);
            if (connectionStackSize <= 0)
                return;

            entity.Connection &= ~selectedConnection;

            foreach (var face in FacingHelper.Faces(selectedConnection))         //перебираем все грани выделенных кабелей
            {

                var material = entity.AllAparams![face.Index].material;          //индекс материала этой грани
                material = "12345678";

                var connection = selectedConnection & FacingHelper.FromFace(face);                   //берем направления только в этой грани

                if ((entity.Connection & FacingHelper.FromFace(face)) == 0) //если грань осталась пустая
                    entity.AllAparams[face.Index] = new();

                connectionStackSize = FacingHelper.Count(connection);          //сколько на этой грани проводов выронить

                var itemStack = default(ItemStack?);

                var block = new GetAsset().BusAsset(api, entity.Block, material, 1); //берем ассет блока кабеля
                itemStack = new(block, connectionStackSize);


                world.SpawnItemEntity(itemStack, pos.ToVec3d());
            }
        }

        /// <summary>
        /// Взаимодействие с кабелем/переключателем
        /// </summary>
        /// <param name="world"></param>
        /// <param name="byPlayer"></param>
        /// <param name="blockSel"></param>
        /// <returns></returns>
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (this.api is ICoreClientAPI)
                return true;

            //это кабель?
            /*
            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityABus entity)
            {
                var key = CacheDataKey.FromEntity(entity);
                var hitPosition = blockSel.HitPosition;

                var sf = new SelectionFacingCable();
                var selectedFacing = sf.SelectionFacing(key as , hitPosition, entity);  //выделяем грань выключателя


            }
            */
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }


        /// <summary>
        /// Переопределение системной функции выделений
        /// </summary>
        /// <param name="blockAccessor"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos position)
        {
            if (blockAccessor.GetBlockEntity(position) is BlockEntityABus { AllAparams: not null } entity)
            {
                var key = CacheDataKey.FromEntity(entity);

                var boxes = CalculateBoxes(key, BlockABus.SelectionBoxesCache, entity);
                return boxes.Values.ToArray() // копируем значения
                    .SelectMany(x => x)
                    .Distinct()
                    .ToArray();

            }

            return base.GetSelectionBoxes(blockAccessor, position);
        }


        /// <summary>
        /// Переопределение системной функции коллизий
        /// </summary>
        /// <param name="blockAccessor"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos position)
        {
            if (blockAccessor.GetBlockEntity(position) is BlockEntityABus { AllAparams: not null } entity)
            {
                var key = CacheDataKey.FromEntity(entity);

                var boxes = CalculateBoxes(key, BlockABus.CollisionBoxesCache, entity);
                return boxes.Values.ToArray() // копируем значения
                    .SelectMany(x => x)
                    .Distinct()
                    .ToArray();

            }

            return base.GetSelectionBoxes(blockAccessor, position);
        }



        /// <summary>
        /// Помогает рандомизировать шейпы
        /// </summary>
        /// <param name="rand"></param>
        /// <returns></returns>
        private float RndHelp(ref Random rand)
        {
            return (float)((rand.NextDouble() * 0.01F) - 0.005F + 1.0F);
        }



        /// <summary>
        /// Отрисовщик шейпов
        /// </summary>
        /// <param name="sourceMesh"></param>
        /// <param name="lightRgbsByCorner"></param>
        /// <param name="position"></param>
        /// <param name="chunkExtBlocks"></param>
        /// <param name="extIndex3d"></param>
        public override void OnJsonTesselation(ref MeshData sourceMesh, ref int[] lightRgbsByCorner,
            BlockPos position, Vintagestory.API.Common.Block[] chunkExtBlocks, int extIndex3d)
        {
            if (api.World.BlockAccessor.GetBlockEntity(position) is BlockEntityABus entity
                && entity.Connection != Facing.None
                && entity.AllAparams != null
                && entity.Block.Code.ToString().Contains("abus"))
            {
                var key = CacheDataKey.FromEntity(entity);

                if (!MeshDataCache.TryGetValue(key, out var meshData))
                {
                    var origin = new Vec3f(0.5f, 0.5f, 0.5f);
                    

                    foreach (var face in FacingHelper.Faces(Facing.AllAll))
                    {
                        int bufIndex = face.Index; // индекс грани 
                        var eparam = entity.AllAparams![bufIndex];
                        
                        BlockVariantsBus partVariant=null;

                        var connection = key.Connection & FacingHelper.FromFace(face);    //берем направления только в этой грани
                        var count = FacingHelper.Count(connection); // количество проводов в этой грани
                        if (count <= 0)
                            continue; // если проводов нет, пропускаем

                        if (count == 1)
                        {
                            partVariant = new BlockVariantsBus(api, entity.Block, eparam.material, 1);
                        }
                        else if (count == 2)
                        {
                            partVariant = new BlockVariantsBus(api, entity.Block, eparam.material, 2);
                        }
                        else if (count == 3)
                        {
                            partVariant = new BlockVariantsBus(api, entity.Block, eparam.material, 4);
                        }
                        else if (count == 4)
                        {
                            partVariant = new BlockVariantsBus(api, entity.Block, eparam.material, 5);
                        }
                            

                        var (x, y, z) = RotationMap[face];
                        var rotated = partVariant.MeshData?.Clone().Rotate(origin, x * GameMath.DEG2RAD, y * GameMath.DEG2RAD, z * GameMath.DEG2RAD);

                        AddMeshData(ref meshData, rotated);
                    }
                    
                    BlockABus.MeshDataCache[key] = meshData!;
                }

                sourceMesh = meshData ?? sourceMesh;
            }

            base.OnJsonTesselation(ref sourceMesh, ref lightRgbsByCorner, position, chunkExtBlocks, extIndex3d);
        }


        /// <summary>
        /// Просчет коллайдеров (коллизии проводов должны совпадать с коллизиями выделения)
        /// </summary>
        /// <param name="key"></param>
        /// <param name="boxesCache"></param>
        /// <param name="entity"></param>
        /// <returns></returns>
        public static Dictionary<Facing, Cuboidf[]> CalculateBoxes(CacheDataKey key,
            IDictionary<CacheDataKey, Dictionary<Facing, Cuboidf[]>> boxesCache, BlockEntityABus entity)
        {
            if (!boxesCache.TryGetValue(key, out var boxes)
                && entity.Connection != Facing.None
                && entity.Block.Code.ToString().Contains("abus"))
            {
                var origin = new Vec3d(0.5, 0.5, 0.5);
                boxesCache[key] = boxes = new();

                foreach (var face in FacingHelper.Faces(Facing.AllAll))
                {
                    int bufIndex = face.Index; // индекс грани 
                    var eparam = entity.AllAparams![bufIndex];
                    Cuboidf[] partBoxes = null;

                    

                    var connection = key.Connection & FacingHelper.FromFace(face);    //берем направления только в этой грани
                    var count = FacingHelper.Count(connection);
                    if (count<=0)
                        continue;

                    if (count == 1)
                    {
                        partBoxes = new BlockVariantsBus(entity.Api, entity.Block, eparam.material, 1).CollisionBoxes;
                    }
                    else if (count == 2)
                    {
                        partBoxes = new BlockVariantsBus(entity.Api, entity.Block, eparam.material, 2).CollisionBoxes;
                    }
                    else if (count == 3)
                    {
                        partBoxes = new BlockVariantsBus(entity.Api, entity.Block, eparam.material,4).CollisionBoxes;
                    }
                    else if (count == 4)
                    {
                        partBoxes = new BlockVariantsBus(entity.Api, entity.Block, eparam.material, 5).CollisionBoxes;
                    }


                    var (x, y, z) = RotationMap[face];
                    var rotated = partBoxes.Select(b => b.RotatedCopy(x, y, z, origin)).ToArray();

                    AddBoxes(ref boxes, FacingHelper.FromFace(face), rotated);
                }
            }

            // если это не кабель, возвращаем стандартные коллайдеры
            if (!entity.Block.Code.ToString().Contains("abus"))
            {
                boxes = new Dictionary<Facing, Cuboidf[]> { { Facing.NorthAll, entity.Block.CollisionBoxes } };
            }

            return boxes!;
        }


        private static void AddBoxes(ref Dictionary<Facing, Cuboidf[]> cache, Facing key, Cuboidf[] boxes)
        {
            if (cache.ContainsKey(key))
            {
                cache[key] = cache[key].Concat(boxes).ToArray();
            }
            else
            {
                cache[key] = boxes;
            }
        }

        private static void AddMeshData(ref MeshData? sourceMesh, MeshData? meshData)
        {
            if (meshData != null)
            {
                if (sourceMesh != null)
                {
                    sourceMesh.AddMeshData(meshData);
                }
                else
                {
                    sourceMesh = meshData;
                }
            }
        }

        /// <summary>
        /// Получение информации о предмете в инвентаре
        /// </summary>
        /// <param name="inSlot"></param>
        /// <param name="dsc"></param>
        /// <param name="world"></param>
        /// <param name="withDebugInfo"></param>
        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            string text = inSlot.Itemstack.Block.Variant["bit"];
            dsc.AppendLine(Lang.Get("Voltage") + ": " + text + " " + Lang.Get("V"));
            
        }


        /// <summary>
        /// Получение подсказок для взаимодействия с блоком
        /// </summary>
        /// <param name="world"></param>
        /// <param name="selection"></param>
        /// <param name="forPlayer"></param>
        /// <returns></returns>
        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        {
            return new WorldInteraction[]
            {
                new WorldInteraction()
                {
                    ActionLangCode = "ThickenCables",
                    HotKeyCode = "shift",
                    MouseButton = EnumMouseButton.Right
                }
            }.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
        }




        /// <summary>
        /// Структура для хранения ключей для словарей
        /// </summary>
        public struct CacheDataKey : IEquatable<CacheDataKey>
        {
            public readonly Facing Connection;
            public readonly AParams[] AllAparams;

            public CacheDataKey(Facing connection, AParams[] allAparams)
            {
                Connection = connection;
                AllAparams = allAparams;
            }

            public static CacheDataKey FromEntity(BlockEntityABus entityE)
            {
                AParams[] bufAllAparams = entityE.AllAparams!.ToArray();
                return new(
                    entityE.Connection,
                    bufAllAparams
                );
            }

            public bool Equals(CacheDataKey other)
            {
                if (Connection != other.Connection ||
                    AllAparams.Length != other.AllAparams.Length)
                    return false;

                for (int i = 0; i < AllAparams.Length; i++)
                {
                    if (!AllAparams[i].Equals(other.AllAparams[i]))
                        return false;
                }

                return true;
            }

            public override bool Equals(object? obj)
            {
                return obj is CacheDataKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + Connection.GetHashCode();
                    foreach (var param in AllAparams)
                    {
                        hash = hash * 31 + param.GetHashCode();
                    }
                    return hash;
                }
            }
        }
    }
}
