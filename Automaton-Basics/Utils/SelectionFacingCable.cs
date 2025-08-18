using Automaton.Content.Block.ABus;
using Automaton.Content.Block.ACable;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using static Automaton.Content.Block.ACable.BlockACable; 


namespace Automaton.Utils
{
    class SelectionFacingCable
    {
        /*
        /// <summary>
        /// Выводит грань выключателя
        /// </summary>
        /// <param name="key"></param>
        /// <param name="hitPosition"></param>
        /// <param name="Cable"></param>
        /// <returns></returns>
        public Facing SelectionFacingSwitch(CacheDataKey key, Vec3d hitPosition, BlockEntity entity)
        {
            var selectedFacing = (
                        from keyValuePair in BlockECable.CalculateBoxes(
                            key,
                            BlockECable.SelectionBoxesCache,
                            entity as BlockEntityECable
                        )
                        let selectionFacing = keyValuePair.Key
                        let selectionBoxes = keyValuePair.Value
                        from selectionBox in selectionBoxes
                        where selectionBox.Clone()
                            .OmniGrowBy(0.005f)
                            .Contains(hitPosition.X, hitPosition.Y, hitPosition.Z)
                        select selectionFacing
                    )
                    .Aggregate(Facing.None, (current, selectionFacing) => current | selectionFacing);


            foreach (var face in FacingHelper.Faces(selectedFacing))
            {
                selectedFacing |= FacingHelper.FromFace(face);
            }

            return selectedFacing;
        }
        */



        /// <summary>
        /// Выводит направления, на которые наведен курсор
        /// </summary>
        /// <param name="key"></param>
        /// <param name="hitPosition"></param>
        /// <param name="entity"></param>
        /// <returns></returns>
        public Facing SelectionFacing(BlockACable.CacheDataKey key, Vec3d hitPosition, BlockEntity? entity)
        {

            var selectedFacing = (
                            from keyValuePair in BlockACable.CalculateBoxes(
                                key,
                                BlockACable.SelectionBoxesCache,
                                (entity as BlockEntityACable)! 
                            )
                            let selectionFacing = keyValuePair.Key
                            let selectionBoxes = keyValuePair.Value
                            from selectionBox in selectionBoxes
                            where selectionBox.Clone()
                                .OmniGrowBy(0.01f)
                                .Contains(hitPosition.X, hitPosition.Y, hitPosition.Z)
                            select selectionFacing
                        )
                        .Aggregate(
                            Facing.None,
                            (current, selectionFacing) =>
                                current | selectionFacing
                    );


            return selectedFacing;
        }



        /// <summary>
        /// Выводит направления, на которые наведен курсор
        /// </summary>
        /// <param name="key"></param>
        /// <param name="hitPosition"></param>
        /// <param name="entity"></param>
        /// <returns></returns>
        public Facing SelectionFacing(BlockABus.CacheDataKey key, Vec3d hitPosition, BlockEntity? entity)
        {

            var selectedFacing = (
                    from keyValuePair in BlockABus.CalculateBoxes(
                        key,
                        BlockABus.SelectionBoxesCache,
                        (entity as BlockEntityABus)!
                    )
                    let selectionFacing = keyValuePair.Key
                    let selectionBoxes = keyValuePair.Value
                    from selectionBox in selectionBoxes
                    where selectionBox.Clone()
                        .OmniGrowBy(0.01f)
                        .Contains(hitPosition.X, hitPosition.Y, hitPosition.Z)
                    select selectionFacing
                )
                .Aggregate(
                    Facing.None,
                    (current, selectionFacing) =>
                        current | selectionFacing
                );


            return selectedFacing;
        }
    }
}
