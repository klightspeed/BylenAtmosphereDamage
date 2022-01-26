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
            Logging.CloseInstance();
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
            _sphere = new BoundingSphereD(_planet.PositionComp.GetPosition(), _planet.AtmosphereRadius);
            
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

        private float GetDamageMultiplier(Vector3D position, Vector3D direction, IMyEntity entity, BoundingBoxD bbox, out IMyEntity shield, out double areaexposed)
        {
            var height = (position - _sphere.Center).Length() - _planet.AverageRadius - _planet.AtmosphereAltitude;
            float fracdmg = 1f;
            areaexposed = 0;
            shield = null;

            if (height > 0)
            {
                fracdmg = (float)Math.Pow(height / Config.RADIATION_FALLOFF_DIST + 1, -2);

                if (Utilities.TryGetEntityExposedArea(position, direction, bbox, entity, _planet, out areaexposed, out shield))
                {
                    fracdmg *= (float)areaexposed * 0.1f;
                }
            }

            return fracdmg;
        }

        private void ProcessDamage()
        {
            foreach (IMyEntity entity in _topEntityCache)
            {
                var grid = entity as IMyCubeGrid;
                if (grid?.Physics != null)
                {
                    if (grid.Closed || grid.MarkedForClose)
                        continue;

                    //Logging.Instance.WriteLine($"Processing grid {entity.EntityId} ({entity.DisplayName})");
                    var height = (entity.WorldVolume.Center - _sphere.Center).Length() - _planet.AverageRadius - _planet.AtmosphereAltitude;

                    var blocks = new List<IMySlimBlock>();
                    grid.GetBlocks(blocks);

                    Vector3D offset = Vector3D.Zero;

                    for (var i = 0; i < Math.Max(1, Math.Min(20, blocks.Count * 3 / 10)); i++)
                    {
                        var direction = entity.WorldVolume.Center - _sphere.Center;
                        direction.Normalize();
                        direction = Utilities.GetRandomGroundFacingDirection(direction, height / _planet.AverageRadius);

                        IMySlimBlock block;

                        try
                        {
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

                            //Logging.Instance.WriteLine($"Processing block at {block.Position} in grid {entity.EntityId} ({entity.DisplayName})");
                        }
                        catch (Exception ex)
                        {
                            Logging.Instance.WriteLine($"Error getting block for grid {entity.EntityId} ({entity.DisplayName}) direction {direction}: {ex}");
                            continue;
                        }

                        float damage;
                        
                        if (height > 0)
                        {
                            damage = grid.GridSizeEnum == MyCubeSize.Small ? Config.SMALL_SHIP_RAD_DAMAGE : Config.LARGE_SHIP_RAD_DAMAGE;
                        }
                        else
                        {
                            damage = grid.GridSizeEnum == MyCubeSize.Small ? Config.SMALL_SHIP_ATMO_DAMAGE : Config.LARGE_SHIP_ATMO_DAMAGE;
                        }

                        try
                        {
                            Vector3D blockpos;
                            block.ComputeWorldCenter(out blockpos);
                            var size = grid.GridSize;
                            var fatblock = block.FatBlock;
                            var localbbox = fatblock?.LocalAABB ?? new BoundingBox(block.Min * size - size / 2f, block.Max * size + size / 2f);
                            IMyEntity shield;
                            double areaexposed;
                            float areamult = GetDamageMultiplier(blockpos, direction, grid, localbbox, out shield, out areaexposed);
                            damage *= areamult;
                            var subtype = block.BlockDefinition.Id.SubtypeName;
                            var funcblk = block.FatBlock as IMyFunctionalBlock;
                            var thruster = block.FatBlock as IMyThrust;
                            float powermult = 0;
                            float curpower = float.NaN;
                            float thrustmult = 0;
                            float curthrust = float.NaN;

                            if (thruster != null && thruster.MaxThrust != 0)
                            {
                                curthrust = thruster.CurrentThrust;
                                thrustmult = 1f + curthrust * 10f / thruster.MaxThrust;
                                damage *= thrustmult;
                            }
                            else if (funcblk != null)
                            {
                                curpower = funcblk.ResourceSink?.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) ?? 0;
                                powermult = 1f + curpower * 5f;
                                curpower *= 1000000f;
                                damage *= powermult;
                            }

                            if (damage > 0.01f)
                            {
                                //Logging.Instance.WriteLine($"Damaging block {block.BlockDefinition.Id} ({block.BlockDefinition.DisplayNameText}) in grid {grid.EntityId} ({grid.CustomName}) with damage {damage} (height={height:0.00}m area={areaexposed:0.00}m² health={block.Integrity * 100 / block.BuildIntegrity:0.0}% ({block.Integrity:0.00} / {block.MaxIntegrity:0.00})" + (powermult == 0 ? "" : $" pwr={curpower:0}W") + (thrustmult == 0 ? "" : $" thrust={curthrust:0}N") + ")" + (shield == null ? "" : $" Shield: {shield.EntityId} ({shield.GetFriendlyName()})"));
                                _damageEntities.AddOrUpdate(block, damage);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logging.Instance.WriteLine($"Error processing block at {block.Position} for grid {entity.EntityId} ({entity.DisplayName}) direction {direction}: {ex}");
                            continue;
                        }
                    }

                    continue;
                }

                var floating = entity as IMyFloatingObject;
                if (floating != null)
                {
                    if (floating.Closed || floating.MarkedForClose)
                        continue;

                    //Logging.Instance.WriteLine($"Processing floating object {entity.EntityId} ({entity.DisplayName})");
                    var height = (entity.WorldVolume.Center - _sphere.Center).Length() - _planet.AverageRadius - _planet.AtmosphereAltitude;
                    var direction = entity.WorldVolume.Center - _sphere.Center;
                    direction.Normalize();
                    direction = Utilities.GetRandomGroundFacingDirection(direction, height / _planet.AverageRadius);
                    IMyEntity shield;
                    double areaexposed;

                    var pos = entity.WorldVolume.Center;
                    var damage = (height > 0 ? Config.SMALL_SHIP_RAD_DAMAGE : Config.SMALL_SHIP_ATMO_DAMAGE) * GetDamageMultiplier(pos, direction, entity, entity.LocalAABB, out shield, out areaexposed);

                    if (damage > 0.01)
                    {
                        //Logging.Instance.WriteLine($"Damaging floating object {floating.EntityId} ({floating.DisplayName}) with damage {damage} (height={height:0.00}m area={areaexposed:0.00}m² integrity={floating.Integrity:0.00})" + (shield == null ? null : $" Shield: {shield.EntityId} ({shield.DisplayName})"));
                        _damageEntities.AddOrUpdate(floating, damage);
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
                    IMyEntity shield;
                    double areaexposed;
                    var damage = (height > 0 ? Config.PLAYER_RAD_DAMAGE : Config.PLAYER_ATMO_DAMAGE) * GetDamageMultiplier(characterPos, direction, character, entity.LocalAABB, out shield, out areaexposed);

                    if (damage > 0.01)
                    {
                        //Logging.Instance.WriteLine($"Damaging character {character.EntityId} ({character.DisplayName}) with damage {damage} (height={height:0.00}m area={areaexposed:0.00}m² health={character.Integrity:0.00})" + (shield == null ? null : $" Shield: {shield.EntityId} ({shield.DisplayName})"));
                        _damageEntities.AddOrUpdate(character, damage);
                    }
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
