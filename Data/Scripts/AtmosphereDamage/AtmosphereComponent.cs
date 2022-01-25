using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace AtmosphericDamage
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Planet), false)]
    public class AtmosphereComponent : MyGameLogicComponent
    {
        private readonly Dictionary<IMySlimBlock, int> _blockParticles = new Dictionary<IMySlimBlock, int>();
        private readonly MyConcurrentDictionary<IMyDestroyableObject, float> _damageEntities = new MyConcurrentDictionary<IMyDestroyableObject, float>();
        private MyStringHash _damageHash;
        private readonly List<LineD> _lines = new List<LineD>();
        private MyPlanet _planet;
        private bool _processing;
        private BoundingSphereD _sphere;
        private List<IMyEntity> _topEntityCache;
        private int _updateCount;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            _planet = Entity as MyPlanet;
            if (_planet == null)
            {
                //TODO: Remove this component from the planet? Might not be worth it
                NeedsUpdate = MyEntityUpdateEnum.NONE;
                return;
            }
            _damageHash = MyStringHash.GetOrCompute(Config.DAMAGE_STRING);
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME | MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void Close()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(Config.PARTICLE_LIST_ID, HandleParticleRequest);
            MyAPIGateway.Utilities.UnregisterMessageHandler(Config.DAMAGE_LIST_ID, HandleDamageRequest);
            MyAPIGateway.Utilities.UnregisterMessageHandler(Config.DRAW_LIST_ID, HandleDrawRequest);
        }

        //Planet might not be fully initialized at Init, so do these on the first frame
        public override void UpdateOnceBeforeFrame()
        {
            if (_planet == null || !_planet.StorageName.StartsWith(Config.PLANET_NAME))
            {
                //TODO: Remove this component from the planet? Might not be worth it
                NeedsUpdate = MyEntityUpdateEnum.NONE;
                return;
            }
            _sphere = new BoundingSphereD(_planet.PositionComp.GetPosition(), _planet.AverageRadius + _planet.AtmosphereAltitude + Config.OVERRIDE_ATMOSPHERE_HEIGHT);
            
            MyAPIGateway.Utilities.RegisterMessageHandler(Config.PARTICLE_LIST_ID, HandleParticleRequest);
            MyAPIGateway.Utilities.RegisterMessageHandler(Config.DAMAGE_LIST_ID, HandleDamageRequest);
            MyAPIGateway.Utilities.RegisterMessageHandler(Config.DRAW_LIST_ID, HandleDrawRequest);
        }

        public override void UpdateBeforeSimulation10()
        {
            if (_processing) //worker thread is busy
                return;

            _updateCount += 10;
            bool processCharacter = _updateCount % 60 == 0;
            bool processPlanet = _updateCount % Config.UPDATE_RATE == 0;

            if (processCharacter || processPlanet)
                MyAPIGateway.Parallel.Start(() =>
                                            {
                                                try
                                                {
                                                    _damageEntities.Clear();
                                                    _topEntityCache = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref _sphere);
                                                    if (processCharacter)
                                                        ProcessCharacterDamage();
                                                    if (processPlanet)
                                                        ProcessDamage();
                                                }
                                                catch (Exception ex)
                                                {
                                                    MyLog.Default.WriteLineAndConsole($"##MOD: Atmospheric component error: {ex}");
                                                    throw;
                                                }
                                                finally
                                                {
                                                    _processing = false;
                                                }
                                            });
        }

        private float GetDamageMultiplier(Vector3D position, Vector3D direction, IMyEntity entity, BoundingBoxD bbox)
        {
            var height = (position - _sphere.Center).Length() - _planet.AverageRadius - _planet.AtmosphereAltitude;
            var fracdmg = (float)Math.Pow(Config.ATMOSPHERE_DAMAGE_EXPONENT, -height / _planet.AtmosphereAltitude);
            double areaexposed;

            if (Utilities.IsEntityInsideGrid(entity))
            {
                fracdmg *= 0.005f;
            }
            else if (height > 0 && Utilities.TryGetEntityExposedArea(position, direction, bbox, entity, _planet, out areaexposed))
            {
                fracdmg *= 0.005f + (float)areaexposed;
            }

            return fracdmg;
        }

        private void ProcessDamage()
        {
            foreach (IMyEntity entity in _topEntityCache)
            {
                var height = (entity.WorldVolume.Center - _sphere.Center).Length() - _planet.AverageRadius - _planet.AtmosphereAltitude;
                var direction = entity.WorldVolume.Center - _sphere.Center;
                direction.Normalize();
                direction = Utilities.GetRandomGroundFacingDirection(direction, height / _planet.AverageRadius);

                var grid = entity as IMyCubeGrid;
                if (grid?.Physics != null)
                {
                    if (grid.Closed || grid.MarkedForClose)
                        continue;


                    var blocks = new List<IMySlimBlock>();
                    grid.GetBlocks(blocks);

                    Vector3D offset = Vector3D.Zero;

                    for (var i = 0; i < Math.Max(1, Math.Min(20, blocks.Count * 3 / 10)); i++)
                    {
                        IMySlimBlock block;

                        if (blocks.Count < 10)
                        {
                            block = blocks.GetRandomItemFromList();
                        }
                        else if (height < 0)
                        {
                            block = Utilities.GetRandomExteriorBlock(grid, blocks);
                        }
                        else
                        {
                            block = Utilities.GetBlockFromDirection(grid, blocks, direction);
                        }

                        if (block == null || _damageEntities.ContainsKey(block))
                            continue;

                        Vector3D blockpos;
                        block.ComputeWorldCenter(out blockpos);
                        var size = grid.GridSize;
                        var fatblock = block.FatBlock;
                        var localbbox = fatblock?.LocalAABB ?? new BoundingBox(block.Min * size - size / 2f, block.Max * size + size / 2f);
                        float damage = grid.GridSizeEnum == MyCubeSize.Small ? Config.SMALL_SHIP_DAMAGE : Config.LARGE_SHIP_DAMAGE;
                        damage *= GetDamageMultiplier(blockpos, direction, grid, localbbox);
                        damage *= block.BuildLevelRatio / Math.Max(block.DamageRatio, 0.01f);
                        var subtype = block.BlockDefinition.Id.SubtypeName;
                        var funcblk = block.FatBlock as IMyFunctionalBlock;

                        if (funcblk != null)
                        {
                            var curpower = funcblk.ResourceSink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId);
                            damage *= 1f + curpower * 5f;
                        }

                        if (damage > 0.1f)
                        {
                            Logging.Instance.WriteLine($"Damaging block {block.BlockDefinition.Id} ({block.BlockDefinition.DisplayNameText}) in grid {grid.EntityId} ({grid.CustomName}) with damage {damage}");
                            _damageEntities.AddOrUpdate(block, damage);
                        }
                    }

                    continue;
                }

                var floating = entity as IMyFloatingObject;
                if (floating != null)
                {
                    if (floating.Closed || floating.MarkedForClose)
                        continue;

                    var pos = entity.WorldVolume.Center;
                    var fracdmg = GetDamageMultiplier(pos, direction, entity, entity.LocalAABB);

                    if (Config.SMALL_SHIP_DAMAGE * fracdmg > 0.1)
                    {
                        Logging.Instance.WriteLine($"Damaging floating object {floating.EntityId} ({floating.DisplayName}) with damage {Config.SMALL_SHIP_DAMAGE * fracdmg}");
                        _damageEntities.AddOrUpdate(floating, Config.SMALL_SHIP_DAMAGE * fracdmg);
                    }

                    Vector3D s = _planet.GetClosestSurfacePointGlobal(ref pos);
                    if (Vector3D.DistanceSquared(pos, s) <= 4 && fracdmg < 1)
                    {
                        Logging.Instance.WriteLine($"Damaging floating object {floating.EntityId} ({floating.DisplayName}) with damage {Config.SMALL_SHIP_DAMAGE}");
                        _damageEntities.AddOrUpdate(floating, Config.SMALL_SHIP_DAMAGE);
                    }
                }
            }
        }

        private void ProcessCharacterDamage()
        {
            foreach (IMyEntity entity in _topEntityCache)
            {
                var character = entity as IMyCharacter;
                if (character != null)
                {
                    if (character.Closed || character.MarkedForClose)
                        continue;


                    Vector3D characterPos = character.WorldVolume.Center;
                    var height = (characterPos - _sphere.Center).Length() - _planet.AverageRadius - _planet.AtmosphereAltitude;
                    var direction = entity.WorldVolume.Center - _sphere.Center;
                    direction.Normalize();
                    direction = Utilities.GetRandomGroundFacingDirection(direction, height / _planet.AverageRadius);
                    var fracdmg = GetDamageMultiplier(characterPos, direction, character, entity.LocalAABB);

                    if (Config.PLAYER_DAMAGE_AMOUNT * fracdmg > 0.1)
                    {
                        Logging.Instance.WriteLine($"Damaging character {character.EntityId} ({character.DisplayName}) with damage {Config.PLAYER_DAMAGE_AMOUNT * fracdmg}");
                        _damageEntities.AddOrUpdate(character, Config.PLAYER_DAMAGE_AMOUNT * fracdmg);
                    }

                    Vector3D surfacePos = _planet.GetClosestSurfacePointGlobal(ref characterPos);

                    if (Vector3D.DistanceSquared(characterPos, surfacePos) < 6.25)
                        _damageEntities.AddOrUpdate(character, Config.PLAYER_DAMAGE_AMOUNT);
                }
            }
        }

        private void SendDrawQueue()
        {
            if (_lines.Any())
                MyAPIGateway.Utilities.InvokeOnGameThread(() => MyAPIGateway.Utilities.SendModMessage(Config.DRAW_LIST_ID, _lines));
        }

        private void HandleDrawRequest(object o)
        {
            //throw new NotImplementedException();
        }

        private void HandleDamageRequest(object o)
        {
            var dic = o as Dictionary<IMyDestroyableObject, float>;
            if (dic == null)
                return;
            foreach (KeyValuePair<IMyDestroyableObject, float> e in _damageEntities)
                dic.AddOrUpdate(e.Key, e.Value);

            _damageEntities.Clear();
        }

        private void HandleParticleRequest(object o)
        {
            var dic = o as Dictionary<IMySlimBlock, int>;
            if (dic == null)
                return; //log? meh.
            foreach (KeyValuePair<IMySlimBlock, int> e in _blockParticles)
                dic[e.Key] = e.Value;
            _blockParticles.Clear();
        }
    }
}
