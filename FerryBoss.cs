using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("FerryBoss", "Shturman", "2.2.2")]
    [Description("Босс-NPC на Ferry Terminal: гранатомёт, свита с ракетными револьверами, электрическое поле, 2 стадии")]
    class FerryBoss : RustPlugin
    {
        #region Fields
        [PluginReference] private Plugin Economics;

        private static FerryBoss Instance;

        private const string PermAdmin = "ferryboss.admin";
        private const string LAUNCH_EFFECT = "assets/bundled/prefabs/fx/weapons/survey_charge/survey_charge_stick.prefab";
        private const string RADIUS_MARKER = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string NAME_MARKER = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

        private ScientistNPC boss;
        private readonly List<ScientistNPC> minions = new List<ScientistNPC>();
        private Vector3 home;
        private bool bossDead;
        private bool unloading;
        private bool phase2;
        private bool transitioning;
        private Item bossWeapon;
        private float gestureUntil;

        // Босс занят (переход во 2 стадию или жест): не стреляет, не ходит и не бьёт током
        private bool IsBossBusy => transitioning || Time.time < gestureUntil;
        private Timer respawnTimer;
        private DateTime nextRespawnAt;

        private MapMarkerGenericRadius radiusMarker;
        private VendingMachineMapMarker nameMarker;
        #endregion

        #region Oxide Hooks
        private void Init()
        {
            Instance = this;
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            // Гранаты одного залпа не должны взрывать друг друга
            Physics.IgnoreLayerCollision((int)Rust.Layer.Reserved1, (int)Rust.Layer.Reserved1, true);
            Physics.IgnoreLayerCollision((int)Rust.Layer.Reserved1, (int)Rust.Layer.Prevent_Building, true);

            ValidatePrefabs();

            // Ждём генерацию навигационной сетки
            timer.Once(config.Spawn.FirstSpawnDelay, () => SpawnBoss(true));
        }

        private void Unload()
        {
            unloading = true;
            respawnTimer?.Destroy();

            foreach (BossController controller in UnityEngine.Object.FindObjectsOfType<BossController>())
                UnityEngine.Object.Destroy(controller);

            foreach (MinionController controller in UnityEngine.Object.FindObjectsOfType<MinionController>())
                UnityEngine.Object.Destroy(controller);

            foreach (GrenadeTrigger trigger in UnityEngine.Object.FindObjectsOfType<GrenadeTrigger>())
                UnityEngine.Object.Destroy(trigger);

            KillAllMinions();

            if (boss != null && !boss.IsDestroyed)
                boss.Kill();

            DestroyMarkers();
            Instance = null;
        }

        private bool IsMinion(BaseNetworkable entity)
        {
            ScientistNPC npc = entity as ScientistNPC;
            return npc != null && minions.Contains(npc);
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null)
                return null;

            BaseEntity initiator = info.Initiator;

            // Урон, нанесённый свитой: ракеты берут урон из конфига, своих не бьют
            if (initiator != null && IsMinion(initiator))
            {
                if ((boss != null && entity == boss) || IsMinion(entity))
                    return true;

                string weaponName = info.WeaponPrefab != null ? info.WeaponPrefab.ShortPrefabName : null;
                if (weaponName != null && weaponName.StartsWith("rocket"))
                {
                    info.damageTypes.Clear();
                    info.damageTypes.Set(Rust.DamageType.Explosion, config.Retinue.RocketDamage);
                }
                return null;
            }

            // Урон от самого босса: себя и свиту не бьёт
            if (initiator != null && boss != null && initiator == boss)
            {
                if (entity == boss || IsMinion(entity))
                    return true;
                return null;
            }

            if (boss == null || entity != boss)
                return null;

            // Во время перехода во 2 стадию босс неуязвим
            if (transitioning && config.Phase.InvulnerableDuringTransition)
                return true;

            if (config.Boss.DamageTakenMultiplier != 1f)
                info.damageTypes.ScaleAll(config.Boss.DamageTakenMultiplier);

            ApplyProtection(info);

            return null;
        }

        // Защита босса от пуль и взрывов: в 1 стадии одно значение, во 2 стадии другое
        private void ApplyProtection(HitInfo info)
        {
            float bullet = phase2 ? config.Phase.BulletProtection : config.Boss.BulletProtection;
            float explosion = phase2 ? config.Phase.ExplosionProtection : config.Boss.ExplosionProtection;

            if (bullet > 0f)
                info.damageTypes.Scale(Rust.DamageType.Bullet, 1f - Mathf.Clamp(bullet, 0f, 100f) / 100f);

            if (explosion > 0f)
                info.damageTypes.Scale(Rust.DamageType.Explosion, 1f - Mathf.Clamp(explosion, 0f, 100f) / 100f);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null)
                return;

            ScientistNPC deadNpc = entity as ScientistNPC;
            if (deadNpc != null && minions.Contains(deadNpc))
            {
                minions.Remove(deadNpc);
                RegisterCorpse(deadNpc, false);
                if (config.Retinue.ClearDefaultLoot)
                    ClearInventory(deadNpc);
                return;
            }

            if (boss == null || entity != boss)
                return;

            bossDead = true;

            Vector3 position = boss.transform.position;
            BasePlayer killer = info?.InitiatorPlayer;

            RegisterCorpse(boss, true);

            if (config.Reward.ClearDefaultLoot)
                ClearInventory(boss);
            else if (!config.Boss.DropWeaponOnDeath)
                boss.inventory.containerBelt.Clear();

            if (!config.Reward.PutIntoCorpse)
                DropRewards(position);

            if (killer != null && !(killer is NPCPlayer) && config.Reward.Money > 0 && Economics != null)
                Economics.Call("Deposit", killer.UserIDString, (double)config.Reward.Money);

            DestroyMarkers();

            if (config.Retinue.KillOnBossDeath)
                KillAllMinions();

            Broadcast("BossKilled", config.Boss.Name, killer != null ? killer.displayName : "?");

            if (!unloading)
            {
                nextRespawnAt = DateTime.UtcNow.AddSeconds(config.Spawn.RespawnSeconds);
                respawnTimer = timer.Once(config.Spawn.RespawnSeconds, () => SpawnBoss(true));
            }
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null)
                return;

            ScientistNPC npc = entity as ScientistNPC;
            if (npc == null)
                return;

            if (minions.Contains(npc))
            {
                minions.Remove(npc);
                return;
            }

            if (boss == null || npc != boss)
                return;

            DestroyMarkers();
            bool wasDead = bossDead;
            boss = null;

            // Босс пропал не из-за смерти (например, удалён) - пробуем вернуть
            if (!wasDead && !unloading)
                respawnTimer = timer.Once(30f, () => SpawnBoss(true));
        }

        #region Corpse loot
        private class PendingCorpse
        {
            public ulong UserId;
            public Vector3 Position;
            public bool IsBoss;
            public float At;
        }

        private readonly List<PendingCorpse> pendingCorpses = new List<PendingCorpse>();

        // Запоминаем смерть NPC, чтобы потом найти его мешок (труп) и положить в него лут из конфига
        private void RegisterCorpse(ScientistNPC npc, bool isBoss)
        {
            pendingCorpses.RemoveAll(p => Time.realtimeSinceStartup - p.At > 30f);
            pendingCorpses.Add(new PendingCorpse
            {
                UserId = npc.userID,
                Position = npc.transform.position,
                IsBoss = isBoss,
                At = Time.realtimeSinceStartup
            });
        }

        private void OnEntitySpawned(LootableCorpse corpse)
        {
            if (corpse == null || pendingCorpses.Count == 0)
                return;

            PendingCorpse match = pendingCorpses.FirstOrDefault(p => p.UserId == corpse.playerSteamID);
            if (match == null)
                match = pendingCorpses.FirstOrDefault(p => Vector3.Distance(p.Position, corpse.transform.position) < 3f);
            if (match == null)
                return;

            pendingCorpses.Remove(match);
            bool isBoss = match.IsBoss;

            // Лут из таблицы NPC добавляется сразу после создания трупа, поэтому заполняем с небольшой задержкой
            timer.Once(0.5f, () => FillCorpse(corpse, isBoss));
        }

        private void FillCorpse(LootableCorpse corpse, bool isBoss)
        {
            if (corpse == null || corpse.IsDestroyed || corpse.containers == null || corpse.containers.Length == 0)
                return;

            bool clear = isBoss ? config.Reward.ClearDefaultLoot : config.Retinue.ClearDefaultLoot;
            List<RewardItem> list = isBoss ? (config.Reward.PutIntoCorpse ? config.Reward.Items : null) : config.Retinue.Items;

            if (clear)
            {
                foreach (ItemContainer container in corpse.containers)
                {
                    if (container != null)
                        container.Clear();
                }
                ItemManager.DoRemoves();
            }

            if (list != null)
            {
                ItemContainer main = corpse.containers.Where(c => c != null).OrderByDescending(c => c.capacity).FirstOrDefault();
                if (main != null)
                {
                    foreach (Item item in RollItems(list))
                    {
                        bool placed = item.MoveToContainer(main);

                        if (!placed)
                        {
                            foreach (ItemContainer other in corpse.containers)
                            {
                                if (other != null && other != main && item.MoveToContainer(other))
                                {
                                    placed = true;
                                    break;
                                }
                            }
                        }

                        // Мешок переполнен - выбрасываем предмет рядом
                        if (!placed)
                            item.Drop(corpse.transform.position + Vector3.up * 0.5f, Vector3.up);
                    }
                }
            }

            corpse.SendNetworkUpdate();
        }
        #endregion

        private void ValidatePrefabs()
        {
            CheckPrefab(config.Boss.Prefab, "Boss / NPC prefab");
            CheckPrefab(config.Weapon.GrenadePrefab, "Grenade launcher / Grenade prefab");
            CheckPrefab(config.Retinue.Prefab, "Retinue / NPC prefab");
            CheckPrefab(config.Retinue.RocketPrefab, "Retinue / Rocket prefab");

            if (!CheckPrefab(config.Phase.TransitionEffect, "Phase 2 / Transition effect prefab"))
                config.Phase.TransitionEffect = string.Empty;

            if (!CheckPrefab(config.Abilities.Electric.ArcEffect, "Electric field / Shock effect prefab"))
                config.Abilities.Electric.ArcEffect = string.Empty;
            if (!CheckPrefab(config.Abilities.Electric.HitEffect, "Electric field / Hit effect prefab"))
                config.Abilities.Electric.HitEffect = string.Empty;
        }

        private bool CheckPrefab(string path, string label)
        {
            if (string.IsNullOrEmpty(path))
                return true;

            if (GameManager.server.FindPrefab(path) != null)
                return true;

            PrintWarning($"Префаб не найден: '{path}' ({label}). Исправьте путь в конфиге.");
            return false;
        }
        #endregion

        #region Helpers
        private static Vector3 GetAimPoint(BasePlayer player)
        {
            return player.transform.position + Vector3.up * (player.modelState != null && player.modelState.ducked ? 0.6f : 1.1f);
        }

        private static BasePlayer FindTarget(Vector3 eye, float range, int mask, bool needLineOfSight)
        {
            BasePlayer best = null;
            float bestDistance = range;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDead() || player.IsSleeping() || player is NPCPlayer)
                    continue;

                float distance = Vector3.Distance(eye, player.transform.position);
                if (distance >= bestDistance)
                    continue;

                if (needLineOfSight && Physics.Linecast(eye, GetAimPoint(player), mask))
                    continue;

                best = player;
                bestDistance = distance;
            }
            return best;
        }

        private void ClearInventory(BasePlayer npc)
        {
            if (npc == null || npc.inventory == null)
                return;

            npc.inventory.containerBelt.Clear();
            npc.inventory.containerWear.Clear();
            npc.inventory.containerMain.Clear();
        }

        private void KillAllMinions()
        {
            foreach (ScientistNPC minion in minions.ToList())
            {
                if (minion != null && !minion.IsDestroyed)
                {
                    ClearInventory(minion);
                    minion.Kill();
                }
            }
            minions.Clear();
        }

        // Квадрат карты (например "N12"), без зависимости от внутренних классов игры
        private string GetGrid(Vector3 position)
        {
            const float cell = 146.3f;
            float size = TerrainMeta.Size.x;
            float offset = size / 2f;
            int max = Mathf.Max(0, Mathf.FloorToInt(size / cell) - 1);

            int x = Mathf.Clamp(Mathf.FloorToInt((position.x + offset) / cell), 0, max);
            int z = Mathf.Clamp(Mathf.FloorToInt((offset - position.z) / cell), 0, max);

            string letters = string.Empty;
            int n = x;
            while (n >= 0)
            {
                letters = (char)('A' + n % 26) + letters;
                n = n / 26 - 1;
            }
            return letters + z;
        }
        #endregion

        #region Discovery (effects and gestures)
        private class GestureInfo
        {
            public string Convar = string.Empty;
            public string Command = string.Empty;
            public string Name = string.Empty;
        }

        private List<string> FindPrefabs(params string[] needles)
        {
            List<string> result = new List<string>();
            foreach (string path in StringPool.toString.Values)
            {
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
                    continue;

                string lower = path.ToLower();
                if (needles.All(n => lower.Contains(n)))
                    result.Add(path);
            }
            return result;
        }

        private string FindArcEffect()
        {
            string[][] searches =
            {
                new[] { "tesla", "effect" },
                new[] { "tesla", "fx" },
                new[] { "electric", "effects" },
                new[] { "electric", "fx" }
            };

            string[] preferred = { "arc", "zap", "spark", "shock", "hit", "discharge" };

            foreach (string[] search in searches)
            {
                List<string> candidates = FindPrefabs(search);
                if (candidates.Count == 0)
                    continue;

                foreach (string candidate in candidates.Take(10))
                    Puts("Кандидат эффекта тока: " + candidate);

                foreach (string word in preferred)
                {
                    string match = candidates.FirstOrDefault(c => c.ToLower().Contains(word));
                    if (match != null)
                        return match;
                }
                return candidates[0];
            }
            return null;
        }

        private static object GetMemberValue(object obj, string name)
        {
            if (obj == null)
                return null;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            Type type = obj.GetType();

            FieldInfo field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(obj);

            PropertyInfo property = type.GetProperty(name, flags);
            return property != null ? property.GetValue(obj, null) : null;
        }

        private static string AsText(object value)
        {
            if (value == null)
                return string.Empty;

            string text = value as string;
            if (text != null)
                return text;

            object english = GetMemberValue(value, "english");
            return english != null ? english.ToString() : value.ToString();
        }

        // Читает список жестов игры (GestureCollection) через reflection, чтобы не зависеть от версии игры
        private List<GestureInfo> ReadGestures()
        {
            List<GestureInfo> list = new List<GestureInfo>();
            try
            {
                Type type = typeof(BasePlayer).Assembly.GetType("GestureCollection");
                if (type == null)
                    return list;

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                object instance = null;
                FieldInfo instanceField = type.GetField("Instance", flags);
                if (instanceField != null)
                    instance = instanceField.GetValue(null);
                else
                {
                    PropertyInfo instanceProperty = type.GetProperty("Instance", flags);
                    if (instanceProperty != null)
                        instance = instanceProperty.GetValue(null, null);
                }

                if (instance == null)
                    return list;

                System.Collections.IEnumerable all = GetMemberValue(instance, "AllGestures") as System.Collections.IEnumerable;
                if (all == null)
                    return list;

                foreach (object gesture in all)
                {
                    if (gesture == null)
                        continue;

                    list.Add(new GestureInfo
                    {
                        Convar = AsText(GetMemberValue(gesture, "convarName")),
                        Command = AsText(GetMemberValue(gesture, "gestureCommand")),
                        Name = AsText(GetMemberValue(gesture, "gestureName"))
                    });
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось прочитать список жестов: " + ex.Message);
            }
            return list;
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (!string.IsNullOrEmpty(value) && !list.Contains(value))
                list.Add(value);
        }

        // Все варианты названия жеста, которые можно передать в Signal.Gesture
        private List<string> ResolveGestureArgs(string configured)
        {
            List<string> args = new List<string>();
            AddUnique(args, configured);

            List<GestureInfo> gestures = ReadGestures();
            string key = (configured ?? string.Empty).ToLower().Replace(" ", string.Empty);

            foreach (GestureInfo g in gestures)
            {
                bool match = g.Convar.ToLower() == key || g.Command.ToLower() == key || g.Name.ToLower().Replace(" ", string.Empty) == key;
                if (!match)
                    continue;

                AddUnique(args, g.Convar);
                AddUnique(args, g.Command);
                AddUnique(args, g.Name);
            }

            // Ничего не нашли по значению из конфига - берём любой жест со словом chest
            if (args.Count <= 1)
            {
                foreach (GestureInfo g in gestures)
                {
                    if (g.Convar.ToLower().Contains("chest") || g.Command.ToLower().Contains("chest") || g.Name.ToLower().Contains("chest"))
                    {
                        AddUnique(args, g.Convar);
                        AddUnique(args, g.Command);
                        AddUnique(args, g.Name);
                    }
                }
            }
            return args;
        }

        // Выбирает ОДНО название для Signal.Gesture: convarName найденного жеста (как у команды "gesture <имя>")
        private string PickGestureArg(string configured)
        {
            List<GestureInfo> gestures = ReadGestures();
            string key = (configured ?? string.Empty).ToLower().Replace(" ", string.Empty);

            GestureInfo found = gestures.FirstOrDefault(g => g.Convar.ToLower() == key || g.Command.ToLower() == key || g.Name.ToLower().Replace(" ", string.Empty) == key);
            if (found == null)
                found = gestures.FirstOrDefault(g => (g.Convar + g.Command + g.Name).ToLower().Contains("chest"));

            if (found == null)
                return configured;
            if (!string.IsNullOrEmpty(found.Convar))
                return found.Convar;
            if (!string.IsNullOrEmpty(found.Command))
                return found.Command;
            return string.IsNullOrEmpty(found.Name) ? configured : found.Name;
        }

        private void PlayBossGesture(string gesture, bool exact = false)
        {
            if (boss == null || boss.IsDestroyed)
                return;

            BaseEntity.Signal signal;
            if (!Enum.TryParse("Gesture", out signal))
            {
                PrintWarning("Signal.Gesture не найден в этой версии игры.");
                return;
            }

            string arg = exact ? gesture : PickGestureArg(gesture);
            Puts($"Жест босса: отправляю '{arg}'");
            boss.SignalBroadcast(signal, arg);
        }

        private void HolsterBossWeapon()
        {
            if (boss != null && !boss.IsDestroyed)
                boss.UpdateActiveItem(default(ItemId));
        }

        private void EquipBossWeapon()
        {
            if (boss != null && !boss.IsDestroyed && bossWeapon != null)
                boss.UpdateActiveItem(bossWeapon.uid);
        }
        #endregion

        #region Spawn
        private MonumentInfo FindMonument()
        {
            if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
                return null;

            string match = config.Spawn.MonumentMatch.ToLower();
            foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
            {
                if (monument == null)
                    continue;

                if (monument.name.ToLower().Contains(match))
                    return monument;

                string display = monument.displayPhrase?.english;
                if (!string.IsNullOrEmpty(display) && display.ToLower().Contains("ferry"))
                    return monument;
            }
            return null;
        }

        // Ищет точку для спавна: сначала на навигационной сетке, иначе просто на земле
        private bool ResolvePoint(Vector3 target, out Vector3 position, out bool onNavMesh)
        {
            position = Vector3.zero;
            onNavMesh = false;

            NavMeshHit hit;
            if (NavMesh.SamplePosition(target, out hit, config.Spawn.SearchRadius, NavMesh.AllAreas))
            {
                position = hit.position;
                onNavMesh = true;
                return true;
            }

            RaycastHit rayHit;
            if (Physics.Raycast(target + Vector3.up * 30f, Vector3.down, out rayHit, 100f, LayerMask.GetMask("Terrain", "World", "Construction", "Default")))
            {
                position = rayHit.point + Vector3.up * 0.2f;
                return true;
            }

            position = new Vector3(target.x, TerrainMeta.HeightMap.GetHeight(target) + 0.2f, target.z);
            return true;
        }

        private bool TryGetSpawnPosition(out Vector3 position, out bool onNavMesh)
        {
            position = Vector3.zero;
            onNavMesh = false;

            MonumentInfo monument = FindMonument();
            if (monument == null)
            {
                PrintWarning("Монумент не найден. Проверьте параметр 'Monument name contains' в конфиге.");
                return false;
            }

            Vector3 target = config.Spawn.UseCustomOffset
                ? monument.transform.TransformPoint(new Vector3(config.Spawn.OffsetX, config.Spawn.OffsetY, config.Spawn.OffsetZ))
                : monument.transform.position;

            return ResolvePoint(target, out position, out onNavMesh);
        }

        private ScientistNPC CreateNpc(string prefab, Vector3 position)
        {
            ScientistNPC npc = GameManager.server.CreateEntity(prefab, position, Quaternion.identity) as ScientistNPC;
            if (npc == null)
            {
                PrintError("Не удалось создать NPC. Проверьте путь к префабу в конфиге: " + prefab);
                return null;
            }

            npc.enableSaving = false;
            npc.Spawn();
            return npc;
        }

        private void SetupNpcBase(ScientistNPC npc, string name, float health, List<ClothingEntry> clothes)
        {
            npc.displayName = name;
            npc.startHealth = health;
            npc.InitializeHealth(health, health);

            npc.inventory.containerBelt.Clear();

            if (clothes != null && clothes.Count > 0)
            {
                npc.inventory.containerWear.Clear();
                foreach (ClothingEntry entry in clothes)
                {
                    Item cloth = ItemManager.CreateByName(entry.Shortname, 1, entry.SkinId);
                    if (cloth == null)
                    {
                        PrintWarning("Неизвестная одежда в конфиге: " + entry.Shortname);
                        continue;
                    }
                    cloth.MoveToContainer(npc.inventory.containerWear);
                }
            }
        }

        private Item EquipWeapon(ScientistNPC npc, Item item)
        {
            if (item == null)
                return null;

            item.MoveToContainer(npc.inventory.containerBelt);
            npc.UpdateActiveItem(item.uid);

            BaseProjectile projectile = item.GetHeldEntity() as BaseProjectile;
            if (projectile != null && projectile.primaryMagazine != null)
                projectile.primaryMagazine.contents = 0;

            return item;
        }

        private bool SpawnBoss(bool retryOnFail, Vector3? customPoint = null)
        {
            if (unloading)
                return false;

            if (boss != null && !boss.IsDestroyed)
                return false;

            Vector3 position;
            bool onNavMesh;
            bool found = customPoint.HasValue
                ? ResolvePoint(customPoint.Value, out position, out onNavMesh)
                : TryGetSpawnPosition(out position, out onNavMesh);

            if (!found)
            {
                PrintWarning("Не удалось найти позицию спавна босса.");
                if (retryOnFail)
                    respawnTimer = timer.Once(30f, () => SpawnBoss(true));
                return false;
            }

            if (!onNavMesh)
                PrintWarning("Рядом нет навигационной сетки: босс будет двигаться встроенным движением плагина (без анимации ходьбы).");

            Puts($"Босс создаётся в точке {position}.");

            ScientistNPC npc = CreateNpc(config.Boss.Prefab, position);
            if (npc == null)
                return false;

            boss = npc;
            bossDead = false;
            phase2 = false;
            transitioning = false;
            home = position;

            timer.Once(0.3f, () => SetupBoss(npc));
            return true;
        }

        private void SetupBoss(ScientistNPC npc)
        {
            if (npc == null || npc.IsDestroyed)
                return;

            SetupNpcBase(npc, config.Boss.Name, config.Boss.Health, config.Boss.Clothes);

            // Гранатомёт в руки босса
            bossWeapon = EquipWeapon(npc, ItemManager.CreateByItemID(config.Weapon.WeaponID, 1, config.Weapon.SkinID));

            npc.gameObject.AddComponent<BossController>();

            if (config.Marker.Enabled)
                CreateMarkers(npc.transform.position);

            Broadcast("BossSpawned", config.Boss.Name, GetGrid(npc.transform.position));
        }
        #endregion

        #region Retinue
        private void SummonRetinue()
        {
            if (boss == null || boss.IsDestroyed || bossDead)
                return;

            minions.RemoveAll(m => m == null || m.IsDestroyed);

            int free = config.Abilities.Summon.MaxAlive - minions.Count;
            int count = Mathf.Min(config.Abilities.Summon.Count, free);
            if (count <= 0)
                return;

            RetinueOptions options = config.Retinue;

            for (int i = 0; i < count; i++)
            {
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float radius = UnityEngine.Random.Range(options.SpawnRadiusMin, Mathf.Max(options.SpawnRadiusMin, options.SpawnRadiusMax));
                Vector3 target = boss.transform.position + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                Vector3 position;
                bool onNavMesh;
                ResolvePoint(target, out position, out onNavMesh);

                ScientistNPC npc = CreateNpc(options.Prefab, position);
                if (npc == null)
                    return;

                minions.Add(npc);
                timer.Once(0.3f, () => SetupMinion(npc));
            }
        }

        private void SetupMinion(ScientistNPC npc)
        {
            if (npc == null || npc.IsDestroyed)
                return;

            RetinueOptions options = config.Retinue;

            SetupNpcBase(npc, options.Name, options.Health, options.Clothes);
            EquipWeapon(npc, ItemManager.CreateByName(options.WeaponShortname, 1, options.WeaponSkin));

            npc.gameObject.AddComponent<MinionController>();
        }
        #endregion

        #region Phase 2
        private void BeginPhase2()
        {
            if (phase2 || boss == null || boss.IsDestroyed)
                return;

            phase2 = true;
            transitioning = true;

            // Убираем оружие из рук и проигрываем жест "удар в грудь"
            if (config.Phase.HolsterWeapon)
                HolsterBossWeapon();
            if (!string.IsNullOrEmpty(config.Phase.Gesture))
                timer.Once(0.4f, () => PlayBossGesture(config.Phase.Gesture, true));

            Broadcast("Phase2", config.Boss.Name);

            float duration = Mathf.Max(0.5f, config.Phase.TransitionSeconds);

            // Визуальный эффект вокруг босса на время перехода (работает, даже если жест не проигрывается)
            if (!string.IsNullOrEmpty(config.Phase.TransitionEffect))
            {
                int ticks = Mathf.Max(1, Mathf.CeilToInt(duration / 0.5f));
                timer.Repeat(0.5f, ticks, () =>
                {
                    if (boss == null || boss.IsDestroyed)
                        return;

                    for (int i = 0; i < config.Phase.TransitionEffectCount; i++)
                        Effect.server.Run(config.Phase.TransitionEffect, boss.transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 1.5f);
                });
            }

            timer.Once(duration, () =>
            {
                transitioning = false;
                EquipBossWeapon();
                if (boss != null && !boss.IsDestroyed && !bossDead && config.Phase.SummonOnPhase2)
                    SummonRetinue();
            });
        }
        #endregion

        #region Rewards
        private void DropRewards(Vector3 position)
        {
            foreach (Item item in RollItems(config.Reward.Items))
                item.Drop(position + Vector3.up * 0.5f, new Vector3(UnityEngine.Random.Range(-1f, 1f), 2f, UnityEngine.Random.Range(-1f, 1f)));
        }

        // Создаёт предметы по списку из конфига (шанс, количество, скин, имя)
        private List<Item> RollItems(List<RewardItem> rewards)
        {
            List<Item> result = new List<Item>();
            foreach (RewardItem reward in rewards)
            {
                if (UnityEngine.Random.Range(0f, 100f) > reward.Chance)
                    continue;

                int amount = UnityEngine.Random.Range(reward.Min, reward.Max + 1);
                if (amount <= 0)
                    continue;

                Item item = ItemManager.CreateByName(reward.Shortname, amount, reward.SkinId);
                if (item == null)
                {
                    PrintWarning("Неизвестный предмет в награде: " + reward.Shortname);
                    continue;
                }

                if (!string.IsNullOrEmpty(reward.DisplayName))
                    item.name = reward.DisplayName;

                result.Add(item);
            }
            return result;
        }
        #endregion

        #region Map Markers
        private void CreateMarkers(Vector3 position)
        {
            DestroyMarkers();

            radiusMarker = GameManager.server.CreateEntity(RADIUS_MARKER, position) as MapMarkerGenericRadius;
            if (radiusMarker != null)
            {
                radiusMarker.enableSaving = false;
                radiusMarker.alpha = 0.6f;
                radiusMarker.color1 = Color.red;
                radiusMarker.color2 = Color.black;
                radiusMarker.radius = 0.25f;
                radiusMarker.Spawn();
                radiusMarker.SendUpdate();
            }

            nameMarker = GameManager.server.CreateEntity(NAME_MARKER, position) as VendingMachineMapMarker;
            if (nameMarker != null)
            {
                nameMarker.enableSaving = false;
                nameMarker.markerShopName = GetMarkerText();
                nameMarker.Spawn();
            }
        }

        private string GetMarkerText()
        {
            return $"{config.Boss.Name} [{Mathf.CeilToInt(boss.health)}/{Mathf.CeilToInt(boss.MaxHealth())}]";
        }

        private void UpdateMarkers()
        {
            if (boss == null || boss.IsDestroyed || !config.Marker.Enabled)
                return;

            Vector3 position = boss.transform.position;

            if (radiusMarker != null && !radiusMarker.IsDestroyed)
            {
                radiusMarker.transform.position = position;
                radiusMarker.SendUpdate();
            }

            if (nameMarker != null && !nameMarker.IsDestroyed)
            {
                nameMarker.transform.position = position;
                nameMarker.markerShopName = GetMarkerText();
                nameMarker.SendNetworkUpdate();
            }
        }

        private void DestroyMarkers()
        {
            if (radiusMarker != null && !radiusMarker.IsDestroyed)
                radiusMarker.Kill();
            if (nameMarker != null && !nameMarker.IsDestroyed)
                nameMarker.Kill();

            radiusMarker = null;
            nameMarker = null;
        }
        #endregion

        #region Boss Controller
        private class BossController : MonoBehaviour
        {
            private ScientistNPC npc;
            private ConfigData cfg;
            private int losMask;
            private int obstacleMask;
            private int groundMask;

            private float nextShot;
            private float nextLeash;
            private float nextMarker;
            private float nextElectric;
            private float nextSummon;

            private void Awake()
            {
                npc = GetComponent<ScientistNPC>();
                cfg = Instance.config;
                losMask = LayerMask.GetMask("Terrain", "World", "Construction");
                obstacleMask = LayerMask.GetMask("World", "Construction");
                groundMask = LayerMask.GetMask("Terrain", "World", "Construction", "Default");
                nextSummon = Time.time + 5f;

                InvokeRepeating(nameof(Think), 1f, 0.25f);
                InvokeRepeating(nameof(Move), 1f, 0.1f);
            }

            private void OnDestroy() => CancelInvoke();

            private bool IsAlive() => Instance != null && npc != null && !npc.IsDestroyed && !npc.IsDead();

            private bool HasNavMesh()
            {
                NavMeshAgent agent = npc.GetComponent<NavMeshAgent>();
                return agent != null && agent.enabled && agent.isOnNavMesh;
            }

            private void StopNavigator()
            {
                if (npc.Brain != null && npc.Brain.Navigator != null)
                    npc.Brain.Navigator.Stop();
            }

            private void Think()
            {
                if (!IsAlive())
                    return;

                float now = Time.time;

                CheckPhase();

                if (Instance.IsBossBusy)
                {
                    StopNavigator();
                    return;
                }

                if (now >= nextLeash)
                {
                    nextLeash = now + 3f;
                    Leash();
                }

                if (now >= nextMarker)
                {
                    nextMarker = now + 1f;
                    Instance.UpdateMarkers();
                }

                Electric(now);
                SummonCheck(now);
                Attack(now);
            }

            private void CheckPhase()
            {
                if (Instance.phase2 || !cfg.Phase.Enabled)
                    return;

                float threshold = npc.MaxHealth() * cfg.Phase.TriggerPercent / 100f;
                if (npc.health <= threshold)
                    Instance.BeginPhase2();
            }

            private void Leash()
            {
                if (!HasNavMesh())
                    return;

                if (Vector3.Distance(npc.transform.position, Instance.home) <= cfg.Boss.LeashRadius)
                    return;

                if (npc.Brain != null && npc.Brain.Navigator != null)
                    npc.Brain.Navigator.SetDestination(Instance.home, BaseNavigator.NavigationSpeed.Fast);
            }

            // Собственное движение, если рядом нет навигационной сетки
            private void Move()
            {
                if (!IsAlive() || Instance.IsBossBusy || HasNavMesh())
                    return;

                Vector3 position = npc.transform.position;

                Vector3 goal = Vector3.zero;
                bool hasGoal = false;

                if (Vector3.Distance(position, Instance.home) > cfg.Boss.LeashRadius)
                {
                    goal = Instance.home;
                    hasGoal = true;
                }
                else
                {
                    BasePlayer target = FindTarget(position, cfg.Boss.AggroRange, losMask, false);
                    if (target != null)
                    {
                        Vector3 toTarget = target.transform.position - position;
                        toTarget.y = 0f;
                        if (toTarget.magnitude > cfg.Boss.PreferredDistance)
                        {
                            goal = target.transform.position;
                            hasGoal = true;
                        }
                    }
                    else if (Vector3.Distance(position, Instance.home) > 3f)
                    {
                        goal = Instance.home;
                        hasGoal = true;
                    }
                }

                if (!hasGoal)
                    return;

                Vector3 direction = goal - position;
                direction.y = 0f;
                float distance = direction.magnitude;
                if (distance < 0.5f)
                    return;
                direction /= distance;

                float speed = cfg.Boss.MoveSpeed * (Instance.phase2 ? cfg.Phase.MoveSpeedMultiplier : 1f);
                float step = speed * 0.1f;
                Vector3 next = position + direction * step;

                // Препятствие перед боссом
                if (Physics.Raycast(position + Vector3.up * 0.8f, direction, step + 0.6f, obstacleMask))
                    return;

                // Земля под следующей точкой
                RaycastHit hit;
                if (!Physics.Raycast(next + Vector3.up * 2f, Vector3.down, out hit, 6f, groundMask))
                    return;

                if (hit.point.y - position.y > 1.2f)
                    return;

                // Не заходим в воду
                if (TerrainMeta.WaterMap != null && TerrainMeta.WaterMap.GetHeight(hit.point) > hit.point.y + 0.4f)
                    return;

                next.y = hit.point.y;
                npc.transform.position = next;
                npc.SetAimDirection(direction);
                npc.SendNetworkUpdate();
            }

            private void Electric(float now)
            {
                ElectricAbility electric = cfg.Abilities.Electric;
                if (!electric.Enabled || now < nextElectric)
                    return;

                Vector3 center = npc.transform.position;
                if (FindTarget(center, cfg.Boss.AggroRange, losMask, false) == null)
                    return;

                bool castRage = Instance.phase2;
                float interval = electric.Interval * (castRage ? cfg.Phase.ElectricIntervalMultiplier : 1f);
                int pulses = Mathf.Max(1, electric.Pulses);
                nextElectric = now + Mathf.Max(0.5f, interval) + pulses * electric.PulseDelay;

                ElectricPulse();
                for (int p = 1; p < pulses; p++)
                    Invoke(nameof(ElectricPulse), p * electric.PulseDelay);
            }

            private void ElectricPulse()
            {
                if (!IsAlive() || Instance.IsBossBusy)
                    return;

                ElectricAbility electric = cfg.Abilities.Electric;
                Vector3 center = npc.transform.position;
                bool rage = Instance.phase2;

                // Разряды вокруг босса (эффект электрозамка, как в старом плагине)
                if (!string.IsNullOrEmpty(electric.ArcEffect))
                {
                    for (int i = 0; i < electric.Arcs; i++)
                        Effect.server.Run(electric.ArcEffect, center + Vector3.up + UnityEngine.Random.insideUnitSphere * electric.EffectSpread);
                }

                float damage = electric.Damage * (rage ? cfg.Phase.ElectricDamageMultiplier : 1f);

                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (player == null || player.IsDead() || player.IsSleeping() || player is NPCPlayer)
                        continue;

                    if (Vector3.Distance(player.transform.position, center) > electric.Radius)
                        continue;

                    player.Hurt(damage, Rust.DamageType.ElectricShock, npc, false);

                    if (electric.DrainFoodAndWater && player.metabolism != null)
                    {
                        player.metabolism.calories.SetValue(0f);
                        player.metabolism.hydration.SetValue(0f);
                    }

                    if (!string.IsNullOrEmpty(electric.HitEffect))
                    {
                        for (int i = 0; i < electric.Arcs; i++)
                            Effect.server.Run(electric.HitEffect, player.transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * electric.EffectSpread);
                    }
                }
            }

            private void SummonCheck(float now)
            {
                SummonAbility summon = cfg.Abilities.Summon;
                if (!summon.Enabled || now < nextSummon)
                    return;

                if (FindTarget(npc.transform.position, cfg.Boss.AggroRange, losMask, false) == null)
                    return;

                nextSummon = now + Mathf.Max(5f, summon.Cooldown);
                Instance.SummonRetinue();
            }

            private Vector3 GetEyePosition() => npc.transform.position + Vector3.up * 1.5f;

            private void Attack(float now)
            {
                if (now < nextShot)
                    return;

                Vector3 eye = GetEyePosition();
                BasePlayer target = FindTarget(eye, cfg.Weapon.FireRange, losMask, true);
                if (target == null)
                    return;

                bool rage = Instance.phase2;

                Vector3 aimPoint = GetAimPoint(target);
                Vector3 baseDirection = GetLaunchDirection(eye, aimPoint, cfg.Weapon.Velocity);

                Vector3 flat = aimPoint - eye;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f)
                    npc.SetAimDirection(flat.normalized);

                float damageMultiplier = cfg.Weapon.DamageMultiplier * (rage ? cfg.Phase.DamageMultiplier : 1f);
                int count = Mathf.Max(1, cfg.Weapon.GrenadesPerShot + (rage ? cfg.Phase.ExtraGrenades : 0));

                for (int i = 0; i < count; i++)
                    LaunchGrenade(eye, baseDirection, damageMultiplier);

                Effect.server.Run(LAUNCH_EFFECT, eye, Vector3.zero);

                float interval = cfg.Weapon.Interval * (rage ? cfg.Phase.IntervalMultiplier : 1f);
                nextShot = now + Mathf.Max(0.2f, interval);
            }

            // Угол возвышения с учётом гравитации, чтобы граната летела в цель по дуге
            private Vector3 GetLaunchDirection(Vector3 from, Vector3 to, float speed)
            {
                Vector3 delta = to - from;
                float dy = delta.y;
                Vector3 flat = new Vector3(delta.x, 0f, delta.z);
                float distance = flat.magnitude;
                if (distance < 0.1f)
                    return delta.normalized;

                float g = Mathf.Abs(Physics.gravity.y);
                float s = Mathf.Clamp(g * distance / (speed * speed), 0f, 1f);
                float angle = 0.5f * Mathf.Asin(s);
                float elevation = angle + Mathf.Atan2(dy, distance);

                Vector3 forward = flat / distance;
                return (forward * Mathf.Cos(elevation) + Vector3.up * Mathf.Sin(elevation)).normalized;
            }

            private void LaunchGrenade(Vector3 eye, Vector3 baseDirection, float damageMultiplier)
            {
                float spread = Mathf.Max(0f, cfg.Weapon.SpreadDegrees);
                Quaternion deviation = Quaternion.Euler(UnityEngine.Random.Range(-spread, spread), UnityEngine.Random.Range(-spread, spread), 0f);
                Vector3 direction = Quaternion.LookRotation(baseDirection) * deviation * Vector3.forward;

                Vector3 launchPosition = eye + direction * 1.0f;

                BaseEntity grenade = GameManager.server.CreateEntity(cfg.Weapon.GrenadePrefab, launchPosition);
                if (grenade == null)
                    return;

                grenade.creatorEntity = npc;
                grenade.SetVelocity(direction * cfg.Weapon.Velocity);
                grenade.SetAngularVelocity(new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)) * 40f);

                grenade.Spawn();
                grenade.gameObject.AddComponent<GrenadeTrigger>();

                TimedExplosive explosive = grenade.GetComponent<TimedExplosive>();
                if (explosive != null)
                {
                    foreach (Rust.DamageTypeEntry damage in explosive.damageTypes)
                        damage.amount *= damageMultiplier;
                }
            }
        }

        private class MinionController : MonoBehaviour
        {
            private ScientistNPC npc;
            private RetinueOptions cfg;
            private int losMask;
            private float nextShot;

            private void Awake()
            {
                npc = GetComponent<ScientistNPC>();
                cfg = Instance.config.Retinue;
                losMask = LayerMask.GetMask("Terrain", "World", "Construction");
                nextShot = Time.time + 2f;
                InvokeRepeating(nameof(Think), 1f, 0.25f);
            }

            private void OnDestroy() => CancelInvoke();

            private void Think()
            {
                if (Instance == null || npc == null || npc.IsDestroyed || npc.IsDead())
                    return;

                float now = Time.time;
                if (now < nextShot)
                    return;

                Vector3 eye = npc.transform.position + Vector3.up * 1.5f;
                BasePlayer target = FindTarget(eye, cfg.FireRange, losMask, true);
                if (target == null)
                    return;

                Vector3 direction = (GetAimPoint(target) - eye).normalized;

                float spread = Mathf.Max(0f, cfg.SpreadDegrees);
                Quaternion deviation = Quaternion.Euler(UnityEngine.Random.Range(-spread, spread), UnityEngine.Random.Range(-spread, spread), 0f);
                direction = Quaternion.LookRotation(direction) * deviation * Vector3.forward;

                Vector3 flat = direction;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f)
                    npc.SetAimDirection(flat.normalized);

                FireRocket(eye, direction);
                nextShot = now + Mathf.Max(0.5f, cfg.Interval);
            }

            // Револьвер стреляет ракетой (как в UberGun)
            private void FireRocket(Vector3 eye, Vector3 direction)
            {
                BaseEntity rocket = GameManager.server.CreateEntity(cfg.RocketPrefab, eye + direction * 1f, Quaternion.LookRotation(direction));
                if (rocket == null)
                    return;

                rocket.creatorEntity = npc;
                rocket.SendMessage("InitializeVelocity", direction * cfg.RocketSpeed);
                rocket.Spawn();
                rocket.ClientRPC(RpcTarget.NetworkGroup("RPCFire"));
            }
        }

        private class GrenadeTrigger : MonoBehaviour
        {
            private TimedExplosive explosive;
            private bool isArmed;

            private void Awake()
            {
                explosive = GetComponent<TimedExplosive>();
                explosive.gameObject.layer = (int)Rust.Layer.Reserved1;

                if (Instance.config.Weapon.ExplodeOnImpact)
                    explosive.SetFuse(15f);

                enabled = false;
                explosive.Invoke(Arm, Instance.config.Weapon.ArmTime);
            }

            private void OnDestroy()
            {
                if (explosive != null && !explosive.IsDestroyed)
                    explosive.Kill();
            }

            private bool exploding;

            private void Arm() => isArmed = true;

            private void OnCollisionEnter(Collision col)
            {
                if (!isArmed || !Instance.config.Weapon.ExplodeOnImpact)
                    return;

                BaseEntity hit = col.GetEntity();
                if (hit != null && explosive.creatorEntity != null && hit == explosive.creatorEntity)
                    return;

                if (exploding)
                    return;

                // Взрыв откладываем: уничтожать объекты прямо внутри физического коллбэка нельзя
                exploding = true;
                explosive.Invoke(explosive.Explode, 0.01f);
            }
        }
        #endregion

        #region Commands
        private bool IsAdmin(BasePlayer player) => player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin);

        private string RunAdminCommand(string action, string[] args, BasePlayer player)
        {
            switch (action)
            {
                case "spawn":
                    if (boss != null && !boss.IsDestroyed)
                        return "Босс уже на карте.";
                    respawnTimer?.Destroy();
                    return SpawnBoss(false) ? "Босс создаётся." : "Не удалось создать босса, смотрите консоль.";

                case "summon":
                    if (player == null)
                        return "Эту команду нужно выполнять в игре.";

                    respawnTimer?.Destroy();

                    // Если босс уже есть - убираем его, чтобы призвать заново рядом с вами
                    if (boss != null && !boss.IsDestroyed)
                    {
                        bossDead = true;
                        KillAllMinions();
                        boss.Kill();
                    }

                    Vector3 forward = player.eyes.HeadForward();
                    forward.y = 0f;
                    Vector3 point = player.transform.position + forward.normalized * 4f;

                    return SpawnBoss(false, point) ? "Босс призван рядом с вами." : "Не удалось призвать босса, смотрите консоль.";

                case "gestures":
                    List<GestureInfo> gestureList = ReadGestures();
                    if (gestureList.Count == 0)
                        return "Список жестов прочитать не удалось.";

                    foreach (GestureInfo g in gestureList)
                        Puts($"Жест: convar='{g.Convar}' command='{g.Command}' name='{g.Name}'");

                    string chestLines = string.Join(" | ", gestureList
                        .Where(g => (g.Convar + g.Command + g.Name).ToLower().Contains("chest"))
                        .Select(g => $"convar='{g.Convar}' command='{g.Command}' name='{g.Name}'")
                        .Take(5).ToArray());

                    return $"Жестов найдено: {gestureList.Count}, полный список в консоли сервера. Со словом chest: {(chestLines.Length > 0 ? chestLines : "нет")}";

                case "gesture":
                    if (boss == null || boss.IsDestroyed)
                        return "Босса сейчас нет на карте.";

                    string gestureArg = args.Length > 1 ? args[1] : config.Phase.Gesture;
                    bool exactGesture = true;
                    float gestureTime = Mathf.Max(3f, config.Phase.TransitionSeconds);

                    // На время жеста босс не стреляет, не ходит и не бьёт током
                    gestureUntil = Time.time + gestureTime + 0.5f;
                    if (config.Phase.HolsterWeapon)
                        HolsterBossWeapon();
                    timer.Once(0.4f, () => PlayBossGesture(gestureArg, exactGesture));
                    timer.Once(gestureTime, EquipBossWeapon);
                    return $"Жест отправлен как '{(exactGesture ? gestureArg : PickGestureArg(gestureArg))}'. Босс не стреляет {gestureTime:F0} сек.";

                case "effects":
                    string needle = args.Length > 1 ? args[1].ToLower() : "tesla";
                    List<string> prefabs = FindPrefabs(needle, "effect");
                    if (prefabs.Count == 0)
                        prefabs = FindPrefabs(needle);

                    foreach (string path in prefabs.Take(40))
                        Puts("Префаб: " + path);

                    return $"Найдено префабов: {prefabs.Count} (первые 40 в консоли сервера).";

                case "setgesture":
                    if (args.Length < 2)
                        return $"Сейчас в конфиге: '{config.Phase.Gesture}'. Использование: /boss setgesture <название>";

                    config.Phase.Gesture = args[1];
                    SaveConfig();
                    return $"Жест для 2 стадии сохранён: '{args[1]}'.";

                case "phase2":
                    if (boss == null || boss.IsDestroyed)
                        return "Босса сейчас нет на карте.";
                    if (phase2)
                        return "Босс уже во 2 стадии.";

                    BeginPhase2();
                    return $"2 стадия запущена принудительно (жест из конфига: '{config.Phase.Gesture}').";

                case "retinue":
                    if (boss == null || boss.IsDestroyed)
                        return "Босса сейчас нет на карте.";
                    SummonRetinue();
                    return "Свита призвана (с учётом лимита живых).";

                case "kill":
                    if (boss == null || boss.IsDestroyed)
                        return "Босса сейчас нет на карте.";
                    bossDead = true;
                    respawnTimer?.Destroy();
                    KillAllMinions();
                    boss.Kill();
                    return "Босс удалён. Автоматического респавна не будет, используйте spawn.";

                case "setpos":
                    if (player == null)
                        return "Эту команду нужно выполнять в игре.";
                    MonumentInfo monument = FindMonument();
                    if (monument == null)
                        return "Монумент Ferry Terminal не найден на карте.";
                    Vector3 local = monument.transform.InverseTransformPoint(player.transform.position);
                    config.Spawn.UseCustomOffset = true;
                    config.Spawn.OffsetX = local.x;
                    config.Spawn.OffsetY = local.y;
                    config.Spawn.OffsetZ = local.z;
                    SaveConfig();
                    return $"Точка спавна сохранена: {local.x:F1} {local.y:F1} {local.z:F1} (относительно монумента).";

                case "info":
                    if (boss != null && !boss.IsDestroyed)
                        return $"Босс жив: HP {Mathf.CeilToInt(boss.health)}/{Mathf.CeilToInt(boss.MaxHealth())}, стадия {(phase2 ? 2 : 1)}, свита {minions.Count}, квадрат {GetGrid(boss.transform.position)}.";
                    double left = (nextRespawnAt - DateTime.UtcNow).TotalSeconds;
                    return left > 0 ? $"Босса нет. Респавн через {Mathf.CeilToInt((float)left)} сек." : "Босса нет на карте.";
            }
            return "Использование: spawn | summon | retinue | kill | setpos | info | gestures | gesture <название> | setgesture <название> | phase2 | effects <слово>";
        }

        [ChatCommand("boss")]
        private void CmdBoss(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;

            string action = args.Length > 0 ? args[0].ToLower() : string.Empty;
            player.ChatMessage(RunAdminCommand(action, args, player));
        }

        [ConsoleCommand("ferryboss")]
        private void CcmdBoss(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !(arg.Player() != null && IsAdmin(arg.Player())))
                return;

            string action = arg.HasArgs() ? arg.GetString(0).ToLower() : string.Empty;
            int argCount = arg.Args != null ? arg.Args.Length : 0;
            string[] consoleArgs = new string[argCount];
            for (int i = 0; i < argCount; i++)
                consoleArgs[i] = arg.GetString(i);

            arg.ReplyWith(RunAdminCommand(action, consoleArgs, arg.Player()));
        }
        #endregion

        #region Localization
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["BossSpawned"] = "<color=#ff4444>[BOSS]</color> {0} has appeared at the Ferry Terminal (grid {1})!",
                ["BossKilled"] = "<color=#ff4444>[BOSS]</color> {0} was killed by {1}!",
                ["Phase2"] = "<color=#ff4444>[BOSS]</color> {0} is enraged!"
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["BossSpawned"] = "<color=#ff4444>[БОСС]</color> {0} появился на Паромном терминале (квадрат {1})!",
                ["BossKilled"] = "<color=#ff4444>[БОСС]</color> {0} убит игроком {1}!",
                ["Phase2"] = "<color=#ff4444>[БОСС]</color> {0} впал в ярость!"
            }, this, "ru");
        }

        private void Broadcast(string key, params object[] args)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                player.ChatMessage(string.Format(lang.GetMessage(key, this, player.UserIDString), args));
        }
        #endregion

        #region Config
        private ConfigData config;

        private class ConfigData
        {
            [JsonProperty("Boss")]
            public BossOptions Boss { get; set; } = new BossOptions();
            [JsonProperty("Spawn")]
            public SpawnOptions Spawn { get; set; } = new SpawnOptions();
            [JsonProperty("Grenade launcher")]
            public WeaponOptions Weapon { get; set; } = new WeaponOptions();
            [JsonProperty("Phase 2 (rage)")]
            public PhaseOptions Phase { get; set; } = new PhaseOptions();
            [JsonProperty("Abilities")]
            public AbilityOptions Abilities { get; set; } = new AbilityOptions();
            [JsonProperty("Retinue (minions with rocket revolvers)")]
            public RetinueOptions Retinue { get; set; } = new RetinueOptions();
            [JsonProperty("Map marker")]
            public MarkerOptions Marker { get; set; } = new MarkerOptions();
            [JsonProperty("Reward")]
            public RewardOptions Reward { get; set; } = new RewardOptions();
        }

        private class ClothingEntry
        {
            [JsonProperty("Shortname")] public string Shortname { get; set; } = string.Empty;
            [JsonProperty("Skin ID")] public ulong SkinId { get; set; } = 0UL;
        }

        private class BossOptions
        {
            [JsonProperty("Name")] public string Name { get; set; } = "Босс Паромного терминала";
            [JsonProperty("NPC prefab")] public string Prefab { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab";
            [JsonProperty("Health")] public float Health { get; set; } = 5000f;
            [JsonProperty("Damage taken multiplier")] public float DamageTakenMultiplier { get; set; } = 1f;
            [JsonProperty("Max distance from spawn point")] public float LeashRadius { get; set; } = 120f;
            [JsonProperty("Drop the grenade launcher on death")] public bool DropWeaponOnDeath { get; set; } = false;
            [JsonProperty("Protection from bullets in phase 1 (%)")] public float BulletProtection { get; set; } = 50f;
            [JsonProperty("Protection from explosions in phase 1 (%)")] public float ExplosionProtection { get; set; } = 50f;
            [JsonProperty("Detection range (players inside it activate abilities and chase)")] public float AggroRange { get; set; } = 80f;
            [JsonProperty("Preferred distance to target (he stops approaching at this distance)")] public float PreferredDistance { get; set; } = 18f;
            [JsonProperty("Move speed in m/s (used when there is no navmesh)")] public float MoveSpeed { get; set; } = 4.5f;
            [JsonProperty("Clothes (empty list = default clothes of the prefab)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ClothingEntry> Clothes { get; set; } = new List<ClothingEntry>();
        }

        private class SpawnOptions
        {
            [JsonProperty("Monument name contains")] public string MonumentMatch { get; set; } = "ferry_terminal";
            [JsonProperty("Use custom offset (set with /boss setpos)")] public bool UseCustomOffset { get; set; } = false;
            [JsonProperty("Offset X")] public float OffsetX { get; set; } = 0f;
            [JsonProperty("Offset Y")] public float OffsetY { get; set; } = 0f;
            [JsonProperty("Offset Z")] public float OffsetZ { get; set; } = 0f;
            [JsonProperty("Navmesh search radius")] public float SearchRadius { get; set; } = 40f;
            [JsonProperty("First spawn delay after server start (sec)")] public float FirstSpawnDelay { get; set; } = 60f;
            [JsonProperty("Respawn delay after death (sec)")] public float RespawnSeconds { get; set; } = 1800f;
        }

        private class WeaponOptions
        {
            [JsonProperty("Weapon item ID")] public int WeaponID { get; set; } = 795371088;
            [JsonProperty("Weapon skin ID")] public ulong SkinID { get; set; } = 1252885494;
            [JsonProperty("Grenade prefab")] public string GrenadePrefab { get; set; } = "assets/prefabs/weapons/f1 grenade/grenade.f1.deployed.prefab";
            [JsonProperty("Fire range")] public float FireRange { get; set; } = 50f;
            [JsonProperty("Interval between shots (sec)")] public float Interval { get; set; } = 2.5f;
            [JsonProperty("Grenades per shot")] public int GrenadesPerShot { get; set; } = 3;
            [JsonProperty("Spread angle (degrees)")] public float SpreadDegrees { get; set; } = 4f;
            [JsonProperty("Grenade velocity")] public float Velocity { get; set; } = 30f;
            [JsonProperty("Grenade damage multiplier")] public float DamageMultiplier { get; set; } = 1.5f;
            [JsonProperty("Grenades explode on impact")] public bool ExplodeOnImpact { get; set; } = true;
            [JsonProperty("Time before grenade is armed (sec)")] public float ArmTime { get; set; } = 0.2f;
        }

        private class PhaseOptions
        {
            [JsonProperty("Enable phase 2")] public bool Enabled { get; set; } = true;
            [JsonProperty("Phase 2 starts at health (%)")] public float TriggerPercent { get; set; } = 50f;
            [JsonProperty("Gesture command played at the start of phase 2 (chest beating)")] public string Gesture { get; set; } = "thumbsup";
            [JsonProperty("Transition duration (sec)")] public float TransitionSeconds { get; set; } = 5f;
            [JsonProperty("Holster the weapon during the transition")] public bool HolsterWeapon { get; set; } = true;
            [JsonProperty("Effect prefab played around the boss during the transition (empty = none)")] public string TransitionEffect { get; set; } = "assets/prefabs/locks/keypad/effects/lock.code.shock.prefab";
            [JsonProperty("Effects per half second during the transition")] public int TransitionEffectCount { get; set; } = 6;
            [JsonProperty("Protection from bullets in phase 2 (%)")] public float BulletProtection { get; set; } = 75f;
            [JsonProperty("Protection from explosions in phase 2 (%)")] public float ExplosionProtection { get; set; } = 75f;
            [JsonProperty("Invulnerable during the transition")] public bool InvulnerableDuringTransition { get; set; } = true;
            [JsonProperty("Shot interval multiplier (lower = faster shooting)")] public float IntervalMultiplier { get; set; } = 0.5f;
            [JsonProperty("Grenade damage multiplier")] public float DamageMultiplier { get; set; } = 2f;
            [JsonProperty("Extra grenades per shot")] public int ExtraGrenades { get; set; } = 2;
            [JsonProperty("Move speed multiplier")] public float MoveSpeedMultiplier { get; set; } = 1.4f;
            [JsonProperty("Electric damage multiplier")] public float ElectricDamageMultiplier { get; set; } = 1.5f;
            [JsonProperty("Electric interval multiplier (lower = more often)")] public float ElectricIntervalMultiplier { get; set; } = 0.6f;
            [JsonProperty("Summon retinue when phase 2 starts")] public bool SummonOnPhase2 { get; set; } = true;
        }

        private class AbilityOptions
        {
            [JsonProperty("Summon retinue")] public SummonAbility Summon { get; set; } = new SummonAbility();
            [JsonProperty("Electric field")] public ElectricAbility Electric { get; set; } = new ElectricAbility();
        }

        private class SummonAbility
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("Minions summoned at once")] public int Count { get; set; } = 2;
            [JsonProperty("Max minions alive at the same time")] public int MaxAlive { get; set; } = 4;
            [JsonProperty("Cooldown (sec)")] public float Cooldown { get; set; } = 60f;
        }

        private class ElectricAbility
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("Radius")] public float Radius { get; set; } = 12f;
            [JsonProperty("Interval (sec)")] public float Interval { get; set; } = 5f;
            [JsonProperty("Electric damage per pulse")] public float Damage { get; set; } = 15f;
            [JsonProperty("Set food and water of players to zero")] public bool DrainFoodAndWater { get; set; } = true;
            [JsonProperty("Pulses per cast")] public int Pulses { get; set; } = 5;
            [JsonProperty("Delay between pulses (sec)")] public float PulseDelay { get; set; } = 1f;
            [JsonProperty("Shock effects per pulse (around the boss and around each hit player)")] public int Arcs { get; set; } = 10;
            [JsonProperty("Shock effect spread radius")] public float EffectSpread { get; set; } = 1.5f;
            [JsonProperty("Shock effect prefab around the boss (empty = no visual)")] public string ArcEffect { get; set; } = "assets/prefabs/locks/keypad/effects/lock.code.shock.prefab";
            [JsonProperty("Shock effect prefab around a hit player (empty = none)")] public string HitEffect { get; set; } = "assets/prefabs/locks/keypad/effects/lock.code.shock.prefab";
        }

        private class RetinueOptions
        {
            [JsonProperty("Name")] public string Name { get; set; } = "Телохранитель босса";
            [JsonProperty("NPC prefab")] public string Prefab { get; set; } = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
            [JsonProperty("Health")] public float Health { get; set; } = 300f;
            [JsonProperty("Weapon shortname")] public string WeaponShortname { get; set; } = "pistol.revolver";
            [JsonProperty("Weapon skin ID")] public ulong WeaponSkin { get; set; } = 0UL;
            [JsonProperty("Rocket prefab")] public string RocketPrefab { get; set; } = "assets/prefabs/ammo/rocket/rocket_basic.prefab";
            [JsonProperty("Rocket speed")] public float RocketSpeed { get; set; } = 60f;
            [JsonProperty("Rocket damage")] public float RocketDamage { get; set; } = 60f;
            [JsonProperty("Interval between shots (sec)")] public float Interval { get; set; } = 3.5f;
            [JsonProperty("Fire range")] public float FireRange { get; set; } = 45f;
            [JsonProperty("Spread angle (degrees)")] public float SpreadDegrees { get; set; } = 1.5f;
            [JsonProperty("Spawn radius min (around the boss)")] public float SpawnRadiusMin { get; set; } = 3f;
            [JsonProperty("Spawn radius max (around the boss)")] public float SpawnRadiusMax { get; set; } = 6f;
            [JsonProperty("Remove the default corpse loot of minions (loot table, clothes, weapon)")] public bool ClearDefaultLoot { get; set; } = true;
            [JsonProperty("Loot in the minion corpse bag", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RewardItem> Items { get; set; } = new List<RewardItem>
            {
                new RewardItem { Shortname = "scrap", Min = 10, Max = 30, Chance = 100f },
                new RewardItem { Shortname = "ammo.pistol", Min = 12, Max = 24, Chance = 50f }
            };
            [JsonProperty("Kill all minions when the boss dies")] public bool KillOnBossDeath { get; set; } = true;
            [JsonProperty("Clothes (empty list = default clothes of the prefab)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ClothingEntry> Clothes { get; set; } = new List<ClothingEntry>();
        }

        private class MarkerOptions
        {
            [JsonProperty("Show boss name and health on the map")] public bool Enabled { get; set; } = true;
        }

        private class RewardOptions
        {
            [JsonProperty("Economics money for the killer (0 - disabled)")] public int Money { get; set; } = 0;
            [JsonProperty("Put the items into the boss corpse bag (false = drop them on the ground)")] public bool PutIntoCorpse { get; set; } = true;
            [JsonProperty("Remove the default corpse loot of the boss (loot table, clothes, weapon)")] public bool ClearDefaultLoot { get; set; } = true;
            [JsonProperty("Loot in the boss corpse bag", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RewardItem> Items { get; set; } = new List<RewardItem>();
        }

        private class RewardItem
        {
            [JsonProperty("Shortname")] public string Shortname { get; set; } = string.Empty;
            [JsonProperty("Skin ID")] public ulong SkinId { get; set; } = 0UL;
            [JsonProperty("Custom name (empty = default)")] public string DisplayName { get; set; } = string.Empty;
            [JsonProperty("Min amount")] public int Min { get; set; } = 1;
            [JsonProperty("Max amount")] public int Max { get; set; } = 1;
            [JsonProperty("Chance (%)")] public float Chance { get; set; } = 100f;
        }

        private ConfigData GetDefaultConfig()
        {
            ConfigData data = new ConfigData();

            data.Reward.Items = new List<RewardItem>
            {
                new RewardItem { Shortname = "scrap", Min = 200, Max = 400, Chance = 100f },
                new RewardItem { Shortname = "metal.refined", Min = 50, Max = 100, Chance = 100f },
                new RewardItem { Shortname = "ammo.rifle", Min = 100, Max = 200, Chance = 60f },
                new RewardItem { Shortname = "rifle.ak", Min = 1, Max = 1, Chance = 15f }
            };

            return data;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<ConfigData>();
                if (config == null)
                    throw new Exception("empty config");
            }
            catch
            {
                PrintWarning("Конфиг повреждён, создаю новый.");
                config = GetDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => config = GetDefaultConfig();

        protected override void SaveConfig() => Config.WriteObject(config, true);
        #endregion
    }
}
