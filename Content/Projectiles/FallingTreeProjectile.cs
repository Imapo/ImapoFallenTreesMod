using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using ImapoFallingTrees.Common;
using ImapoFallingTrees.Common.GlobalTiles;

namespace ImapoFallingTrees.Content.Projectiles
{
    public class FallingTreeProjectile : ModProjectile
    {
        private enum Phase
        {
            Warmup,
            Falling,
            Bounce,
            Landed,
            Shattering
        }

        private int TreeHeightTiles => (int)Projectile.ai[0];
        private int Direction => (int)Projectile.ai[1];

        // Небольшая защита на случай, если в твоей версии Terraria/ModLoader
        // массив Projectile.ai имеет только 2 элемента.
        private int dropItemTypeFallback = ItemID.Wood;

        private int DropItemType
        {
            get
            {
                if (Projectile.ai.Length > 2)
                    return (int)Projectile.ai[2];

                return dropItemTypeFallback;
            }
        }

        private void SetDropItemType(int value)
        {
            dropItemTypeFallback = value;

            if (Projectile.ai.Length > 2)
                Projectile.ai[2] = value;
        }

        // Минимальная физическая высота нужна, чтобы короткие деревья
        // не давали отрицательную или нулевую длину для проверок коллизий.
        private int PhysicalHeightPixels => Math.Max(16, (TreeHeightTiles - 2) * 16);

        private const bool DebugDraw = false;

        private Phase CurrentPhase
        {
            get => (Phase)(int)Projectile.localAI[0];
            set => Projectile.localAI[0] = (int)value;
        }

        private float PhaseTimer
        {
            get => Projectile.localAI[1];
            set => Projectile.localAI[1] = value;
        }

        private float ChopProgress
        {
            get => Projectile.knockBack;
            set => Projectile.knockBack = value;
        }

        public float SavedAngle => Angle;
        public float SavedChopProgress => ChopProgress;

        private static readonly Dictionary<int, List<TrunkFrameData>> SavedFrames = new();

        private float Angle;
        private float AngularVelocity;
        private Texture2D treeTexture;
        private int pivotX, pivotY;

        private int ShatterTimer;
        private bool ShatterSoundPlayed;
        private int LandedTicks;

        // Поля для защиты от застревания.
        private float lastSafeAngle;
        private float lastResumeAngle;
        private int resumeAttempts;
        private int blockedSupportChecks;
        private float chopCooldown;

        private float restAngle;

        private const int WarmupTicks = 60;
        private const int BounceTicks = 26;
        private const float BounceAmplitude = 0.11f;
        private const float Gravity = 0.0008f;

        private const float MaxAngle = MathHelper.Pi * 0.9f;
        private const float DestructionAngle = MathHelper.Pi * 7f / 9f;

        private const int SupportCheckInterval = 30;
        private const float RequiredChopDamage = 100f;

        private const int ShatterDuration = 90;
        private const int ShatterParticleInterval = 8;

        // Настройки антизастревания.
        private const float AngleEpsilon = 0.02f;
        private const float UnstuckAngleProgress = 0.25f;
        private const int MaxResumeAttempts = 6;
        private const int MaxBlockedSupportChecks = 20;

        private static readonly SoundStyle[] TreeFallSounds = new SoundStyle[]
        {
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall1") { MaxInstances = 1 },
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall2") { MaxInstances = 1 },
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall3") { MaxInstances = 1 },
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall4") { MaxInstances = 1 },
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall5") { MaxInstances = 1 }
        };

        private static readonly SoundStyle TreeImpactSound =
            new SoundStyle("ImapoFallingTrees/Sounds/TreeImpact") { MaxInstances = 1 };

        private struct ShatterFragment
        {
            public Rectangle Source;
            public float Distance;
            public Vector2 Offset;
            public Vector2 Velocity;
            public float Rotation;
            public float Spin;
        }

        private ShatterFragment[] shatterFragments;
        private readonly HashSet<int> hitPlayers = new HashSet<int>();
        private readonly HashSet<int> hitNPCs = new HashSet<int>();

        private bool fallSoundPlayed = false;
        private bool impactSoundPlayed = false;

        public override string Texture => "ImapoFallingTrees/Content/Projectiles/FallingTreeProjectile";

        public void Init(int heightTiles, int direction, Texture2D composite, int pivotXIn, int pivotYIn, int dropItemType)
        {
            shatterFragments = null;
            Projectile.ai[0] = heightTiles;
            Projectile.ai[1] = direction;
            SetDropItemType(dropItemType);

            CurrentPhase = Phase.Warmup;
            PhaseTimer = 0f;

            chopCooldown = 0f;
            ChopProgress = 0f;

            Angle = 0f;
            AngularVelocity = 0f;

            treeTexture = composite;
            pivotX = pivotXIn;
            pivotY = pivotYIn;

            fallSoundPlayed = false;
            impactSoundPlayed = false;

            lastSafeAngle = 0f;
            lastResumeAngle = 0f;
            resumeAttempts = 0;
            blockedSupportChecks = 0;
        }

        public void InitFromSave(
            int heightTiles,
            int direction,
            Texture2D composite,
            int pivotXIn,
            int pivotYIn,
            int dropItemType,
            float savedAngle,
            float savedChopProgress)
        {
            shatterFragments = null;
            Projectile.ai[0] = heightTiles;
            Projectile.ai[1] = direction;
            SetDropItemType(dropItemType);

            lastSafeAngle = savedAngle;
            lastResumeAngle = savedAngle;
            resumeAttempts = 0;
            blockedSupportChecks = 0;

            if (savedAngle >= DestructionAngle)
            {
                Projectile.Kill();
                return;
            }

            CurrentPhase = Phase.Landed;
            PhaseTimer = 0f;

            chopCooldown = 0f;
            ChopProgress = savedChopProgress;

            Angle = savedAngle;
            AngularVelocity = 0f;

            treeTexture = composite;
            pivotX = pivotXIn;
            pivotY = pivotYIn;

            fallSoundPlayed = true;
            impactSoundPlayed = true;
        }

        public static void SaveFrames(int projId, List<TrunkFrameData> frames)
        {
            SavedFrames[projId] = frames;
        }

        public static List<TrunkFrameData> GetFrames(int projId)
        {
            return SavedFrames.TryGetValue(projId, out var frames) ? frames : null;
        }

        public static void RemoveFrames(int projId)
        {
            SavedFrames.Remove(projId);
        }

        public override void SetDefaults()
        {
            Projectile.width = 32;
            Projectile.height = 32;

            Projectile.friendly = false;
            Projectile.hostile = false;
            Projectile.tileCollide = false;
            Projectile.ignoreWater = true;
            Projectile.penetrate = -1;

            Projectile.timeLeft = 3600;
            Projectile.netImportant = true;
        }

        public override bool ShouldUpdatePosition() => false;

        public override void AI()
        {
            switch (CurrentPhase)
            {
                case Phase.Warmup:
                    UpdateWarmup();
                    break;

                case Phase.Falling:
                    UpdateFalling();
                    break;

                case Phase.Bounce:
                    UpdateBounce();
                    break;

                case Phase.Landed:
                    UpdateLanded();
                    break;

                case Phase.Shattering:
                    UpdateShattering();
                    break;
            }

            if (chopCooldown > 0f)
                chopCooldown -= 1f;
        }

        private void UpdateWarmup()
        {
            if (PhaseTimer == 0)
            {
                PlayRandomFallSound();
            }

            PhaseTimer++;

            float progress = PhaseTimer / WarmupTicks;
            float amplitude = MathHelper.Lerp(0.01f, 0.045f, progress);

            Angle = (float)Math.Sin(PhaseTimer * 0.16f) * amplitude;

            if (PhaseTimer % 20 == 0)
            {
                Dust.NewDust(Projectile.position, 16, 16, DustID.WoodFurniture, 0f, -1f);
            }

            if (PhaseTimer >= WarmupTicks)
            {
                CurrentPhase = Phase.Falling;
                PhaseTimer = 0f;

                Angle = 0f;
                AngularVelocity = 0.001f;

                lastSafeAngle = Angle;
                lastResumeAngle = Angle;
            }
        }

        private void UpdateFalling()
        {
            int physicalHeight = PhysicalHeightPixels;

            // Если дерево уже находится в коллизии, не даём ему крутиться дальше.
            if (IsBlockedAtAngle(Angle, physicalHeight))
            {
                float backAngle = Math.Max(0f, Angle - AngleEpsilon);

                if (backAngle > 0f && !IsBlockedAtAngle(backAngle, physicalHeight))
                {
                    StartBounce(backAngle);
                }
                else if (Angle <= AngleEpsilon)
                {
                    // Если это самое начало падения, лучше мягко перейти в bounce,
                    // а уже потом пусть решает поддержка/застревание.
                    StartBounce(0f);
                }
                else
                {
                    // Если откатиться некуда и дерево уже глубоко в блоках — ломаем.
                    StartShattering();
                }

                return;
            }

            lastSafeAngle = Angle;

            AngularVelocity += Gravity;
            Angle += AngularVelocity;

            // Если после шага поворота врезались в блок,
            // возвращаемся на последний безопасный угол.
            if (IsBlockedAtAngle(Angle, physicalHeight))
            {
                Angle = Math.Max(0f, lastSafeAngle - 0.001f);
                StartBounce(Angle);
                return;
            }

            if (Angle >= DestructionAngle)
            {
                StartShattering();
                return;
            }

            if (Angle >= MaxAngle)
            {
                StartBounce(MaxAngle);
                return;
            }

            // Если дерево реально продолжило падать и повернулось достаточно сильно,
            // считаем, что оно не застряло.
            if (Angle > lastResumeAngle + UnstuckAngleProgress)
            {
                resumeAttempts = 0;
                blockedSupportChecks = 0;
                lastResumeAngle = Angle;
            }

            DamageEntitiesAlongTrunk(TreeHeightTiles * 16);
            SpawnFallingLeaves(physicalHeight);
        }

        private void UpdateBounce()
        {
            PhaseTimer++;

            float t = PhaseTimer / BounceTicks;
            float damped = (float)(Math.Exp(-t * 5.0) * Math.Sin(t * MathHelper.TwoPi * 1.6));

            Angle = MathHelper.Clamp(restAngle + damped * BounceAmplitude, 0f, MaxAngle);

            if (PhaseTimer >= BounceTicks)
            {
                Angle = restAngle;
                LandTree();
            }
        }

        private void UpdateLanded()
        {
            Projectile.timeLeft = 2;

            PhaseTimer++;
            LandedTicks++;

            if (PhaseTimer >= SupportCheckInterval)
            {
                PhaseTimer = 0f;

                int physicalHeight = PhysicalHeightPixels;

                // Проверка опоры под pivot-точкой.
                if (!HasPivotSupport())
                {
                    ShatterTree();
                    return;
                }

                // Если ствол не имеет поддержки, пробуем продолжить падение только тогда,
                // когда при небольшом увеличении угла нет коллизии.
                if (LandedTicks > 90 && !HasSupport(Angle, physicalHeight))
                {
                    float testAngle = Angle + AngleEpsilon;
                    bool canFallFurther = !IsBlockedAtAngle(testAngle, physicalHeight);

                    if (canFallFurther)
                    {
                        resumeAttempts++;

                        if (resumeAttempts > MaxResumeAttempts)
                        {
                            // Слишком много попыток продолжить падение.
                            StartShattering();
                        }
                        else
                        {
                            ResumeFalling();
                        }
                    }
                    else
                    {
                        // Дерево упирается в блок/текстуру.
                        // Не вызываем ResumeFalling, чтобы не было цикла.

                        blockedSupportChecks++;

                        // Fail-safe: если дерево долго находится в заблокированном состоянии,
                        // ломаем его. Если хочешь, чтобы такие деревья оставались навсегда,
                        // просто убери этот StartShattering().
                        if (blockedSupportChecks > MaxBlockedSupportChecks)
                        {
                            StartShattering();
                        }
                    }

                    return;
                }

                // Если поддержка есть, сбрасываем счётчики застревания.
                resumeAttempts = 0;
                blockedSupportChecks = 0;
            }

            Player player = Main.LocalPlayer;

            if (player?.active != true || player.dead)
                return;

            if (chopCooldown <= 0f && player.HeldItem != null && player.HeldItem.axe > 0 && player.itemAnimation > 0)
            {
                int heightPixels = TreeHeightTiles * 16;

                Vector2 dir = DirectionVector(Angle);
                Vector2 toPlayer = player.Center - Projectile.Center;

                float proj = Vector2.Dot(toPlayer, dir);

                if (proj >= -30f && proj <= heightPixels + 30f)
                {
                    Vector2 closest = Projectile.Center + dir * MathHelper.Clamp(proj, 0f, heightPixels);

                    if (Vector2.Distance(player.Center, closest) < 50f)
                    {
                        ChopProgress += player.HeldItem.axe;
                        chopCooldown = 30f;

                        SpawnChopParticles(closest);
                        SoundEngine.PlaySound(SoundID.Dig, Projectile.Center);

                        if (ChopProgress >= RequiredChopDamage)
                        {
                            ChopDownedTree(player);
                        }
                    }
                }
            }
        }

        private void UpdateShattering()
        {
            ShatterTimer++;

            UpdateShatterFragments();

            if (!ShatterSoundPlayed)
            {
                SoundEngine.PlaySound(SoundID.Dig, Projectile.Center);
                ShatterSoundPlayed = true;
            }

            if (ShatterTimer % ShatterParticleInterval == 0)
            {
                int heightPixels = TreeHeightTiles * 16;
                const int particlePoints = 4;

                for (int p = 0; p <= particlePoints; p++)
                {
                    float dist = heightPixels * (p / (float)particlePoints);
                    Vector2 point = Projectile.Center + DirectionVector(Angle) * dist;

                    int d = Dust.NewDust(
                        point - new Vector2(8, 8),
                        16,
                        16,
                        DustID.WoodFurniture,
                        0f,
                        0f,
                        100,
                        default,
                        Main.rand.NextFloat(1.0f, 1.5f));

                    Main.dust[d].velocity = new Vector2(
                        Main.rand.NextFloat(-3f, 3f),
                        Main.rand.NextFloat(-4f, 1f));

                    Main.dust[d].noGravity = false;

                    if (Main.rand.NextBool(4))
                    {
                        int leaf = Dust.NewDust(
                            point - new Vector2(8, 8),
                            16,
                            16,
                            DustID.WoodFurniture,
                            0f,
                            0f,
                            100,
                            default,
                            1f);

                        Main.dust[leaf].color = new Color(70, 130, 60);
                        Main.dust[leaf].velocity = new Vector2(
                            Main.rand.NextFloat(-2f, 2f),
                            Main.rand.NextFloat(-3f, 0f));
                    }
                }
            }

            if (ShatterTimer >= ShatterDuration)
            {
                Projectile.Kill();
            }
        }

        private void CreateShatterFragments()
        {
            shatterFragments = null;

            if (treeTexture == null)
                return;

            int textureWidth = treeTexture.Width;
            int textureHeight = treeTexture.Height;

            if (textureWidth <= 0 || textureHeight <= 0)
                return;

            // Количество "рядов", на которые режем ствол.
            // Чем выше дерево, тем больше кусков, но ограничиваем ради производительности.
            int rows = (int)MathHelper.Clamp(TreeHeightTiles, 4f, 12f);
            int rowHeight = Math.Max(8, textureHeight / rows);

            var fragments = new List<ShatterFragment>();

            Vector2 trunkDir = DirectionVector(Angle);
            Vector2 perp = new Vector2(-trunkDir.Y, trunkDir.X);

            for (int y = 0; y < textureHeight; y += rowHeight)
            {
                int h = Math.Min(rowHeight, textureHeight - y);

                if (h <= 0)
                    break;

                // Если текстура шириной 16 пикселей, режем каждый ряд ещё и пополам.
                int piecesAcross = textureWidth >= 16 ? 2 : 1;
                int pieceWidth = Math.Max(1, textureWidth / piecesAcross);

                for (int p = 0; p < piecesAcross; p++)
                {
                    int x = p * pieceWidth;
                    int w = (p == piecesAcross - 1) ? (textureWidth - x) : pieceWidth;

                    if (w <= 0)
                        continue;

                    var source = new Rectangle(x, y, w, h);

                    // Расстояние от pivot-точки до центра этого куска.
                    float distance = textureHeight - (y + h * 0.5f);

                    // Чем выше кусок, тем сильнее он улетает.
                    float heightProgress = MathHelper.Clamp(distance / Math.Max(1f, textureHeight), 0f, 1f);

                    Vector2 velocity =
                        trunkDir * Main.rand.NextFloat(-1.5f, 3.5f) +
                        perp * Main.rand.NextFloat(-3.0f, 3.0f) +
                        new Vector2(0f, Main.rand.NextFloat(-2.5f, -0.5f));

                    // Чтобы левые и правые половинки разлетались в стороны.
                    if (piecesAcross > 1)
                    {
                        float sideBias = p == 0 ? -1f : 1f;
                        velocity += perp * sideBias * Main.rand.NextFloat(0.6f, 1.8f);
                    }

                    velocity *= MathHelper.Lerp(0.8f, 2.2f, heightProgress);

                    fragments.Add(new ShatterFragment
                    {
                        Source = source,
                        Distance = distance,
                        Offset = Vector2.Zero,
                        Velocity = velocity,
                        Rotation = Main.rand.NextFloat(-0.08f, 0.08f),
                        Spin = Main.rand.NextFloat(-0.18f, 0.18f)
                    });
                }
            }

            shatterFragments = fragments.ToArray();
        }

        private void UpdateShatterFragments()
        {
            if (shatterFragments == null)
                return;

            for (int i = 0; i < shatterFragments.Length; i++)
            {
                var f = shatterFragments[i];

                // Лёгкая гравитация.
                f.Velocity.Y += 0.16f;

                // Небольшое затухание.
                f.Velocity *= 0.985f;

                f.Offset += f.Velocity;
                f.Rotation += f.Spin;

                shatterFragments[i] = f;
            }
        }

        private bool IsBlockedAtAngle(float angle, int heightPixels)
        {
            if (angle < 0f)
                return true;

            return WouldTipHitObstacle(angle, heightPixels) || WouldTrunkHitGround(angle, heightPixels);
        }

        private void StartBounce(float landedAngle)
        {
            CurrentPhase = Phase.Bounce;
            PhaseTimer = 0f;

            restAngle = MathHelper.Clamp(landedAngle, 0f, MaxAngle);
            Angle = restAngle;

            Vector2 impactPoint = Projectile.Center + DirectionVector(restAngle) * PhysicalHeightPixels;

            if (!impactSoundPlayed)
            {
                PlayImpactSound();
                impactSoundPlayed = true;
            }

            for (int n = 0; n < 12; n++)
            {
                int d = Dust.NewDust(
                    impactPoint - new Vector2(12, 12),
                    24,
                    24,
                    DustID.WoodFurniture,
                    0f,
                    -2f);

                Main.dust[d].velocity *= 1.5f;
            }
        }

        private void LandTree()
        {
            CurrentPhase = Phase.Landed;
            PhaseTimer = 0f;
            LandedTicks = 0;

            Projectile.velocity = Vector2.Zero;

            var frames = GetFrames(Projectile.whoAmI);

            if (frames != null)
            {
                int rootX = (int)(Projectile.Center.X / 16f);
                int rootY = (int)(Projectile.Center.Y / 16f);

                FallenTreesWorld.RegisterTree(
                    Projectile.whoAmI,
                    rootX,
                    rootY,
                    Angle,
                    Direction,
                    DropItemType,
                    0f,
                    TreeHeightTiles,
                    frames);
            }
        }

        private void ResumeFalling()
        {
            CurrentPhase = Phase.Falling;
            PhaseTimer = 0f;

            AngularVelocity = 0.001f;
            Projectile.timeLeft = 3600;

            hitPlayers.Clear();
            hitNPCs.Clear();

            fallSoundPlayed = false;
            impactSoundPlayed = false;

            lastResumeAngle = Angle;

            PlayRandomFallSound();

            for (int n = 0; n < 8; n++)
            {
                Dust.NewDust(
                    Projectile.position,
                    Projectile.width,
                    Projectile.height,
                    DustID.WoodFurniture,
                    0f,
                    -1f,
                    100,
                    default,
                    1f);
            }
        }

        private void StartShattering()
        {
            if (CurrentPhase == Phase.Shattering)
                return;

            CurrentPhase = Phase.Shattering;

            ShatterTimer = 0;
            ShatterSoundPlayed = false;

            Projectile.timeLeft = ShatterDuration + 10;

            CreateShatterFragments();

            SpawnShatterBurst();
            DropWoodOnShatter();
        }

        private void ShatterTree()
        {
            StartShattering();
        }

        private void ChopDownedTree(Player player)
        {
            StartShattering();
        }

        private void SpawnShatterBurst()
        {
            int heightPixels = TreeHeightTiles * 16;
            const int burstPoints = 4;

            for (int p = 0; p <= burstPoints; p++)
            {
                float dist = heightPixels * (p / (float)burstPoints);
                Vector2 point = Projectile.Center + DirectionVector(Angle) * dist;

                for (int n = 0; n < 2; n++)
                {
                    int d = Dust.NewDust(
                        point - new Vector2(12, 12),
                        24,
                        24,
                        DustID.WoodFurniture,
                        0f,
                        0f,
                        100,
                        default,
                        Main.rand.NextFloat(1.2f, 1.8f));

                    Main.dust[d].velocity = new Vector2(
                        Main.rand.NextFloat(-4f, 4f),
                        Main.rand.NextFloat(-5f, 2f));

                    Main.dust[d].noGravity = false;
                }
            }
        }

        private void DropWoodOnShatter()
        {
            int dropType = DropItemType > 0 ? DropItemType : ItemID.Wood;
            int amount = Main.rand.Next(1, 4);

            var source = new EntitySource_Misc("FallenTreeShatter");

            Item.NewItem(
                source,
                (int)Projectile.Center.X,
                (int)Projectile.Center.Y,
                16,
                16,
                dropType,
                amount);
        }

        private Vector2 DirectionVector(float angle)
        {
            float x = (float)Math.Sin(angle) * Direction;
            float y = -(float)Math.Cos(angle);

            return new Vector2(x, y);
        }

        private bool WouldTipHitObstacle(float angle, int heightPixels)
        {
            Vector2 tip = Projectile.Center + DirectionVector(angle) * heightPixels;

            int tileX = (int)Math.Floor(tip.X / 16f);
            int tileY = (int)Math.Floor(tip.Y / 16f);

            if (!WorldGen.InWorld(tileX, tileY))
                return true;

            Tile t = Main.tile[tileX, tileY];

            if (!t.HasTile)
                return false;

            return Main.tileSolid[t.TileType]
                || t.TileType == TileID.Trees
                || t.TileType == TileID.PalmTree;
        }

        private bool WouldTrunkHitGround(float angle, int heightPixels)
        {
            const int checkPoints = 4;

            for (int i = 1; i <= checkPoints; i++)
            {
                float dist = heightPixels * (i / (float)(checkPoints + 1));

                Vector2 point = Projectile.Center + DirectionVector(angle) * dist;

                int tileX = (int)Math.Floor(point.X / 16f);
                int tileY = (int)Math.Floor(point.Y / 16f);

                if (!WorldGen.InWorld(tileX, tileY))
                    continue;

                Tile t = Main.tile[tileX, tileY];

                if (t.HasTile && Main.tileSolid[t.TileType])
                    return true;
            }

            return false;
        }

        private void DamageEntitiesAlongTrunk(int heightPixels)
        {
            const int samples = 6;

            int damage = (int)MathHelper.Lerp(8, 60, AngularVelocity * 100f);

            for (int s = 1; s <= samples; s++)
            {
                float dist = heightPixels * (s / (float)samples);
                Vector2 point = Projectile.Center + DirectionVector(Angle) * dist;

                Rectangle sampleBox = new Rectangle(
                    (int)point.X - 8,
                    (int)point.Y - 8,
                    16,
                    16);

                for (int p = 0; p < Main.maxPlayers; p++)
                {
                    Player pl = Main.player[p];

                    if (!pl.active || pl.dead || hitPlayers.Contains(p))
                        continue;

                    if (sampleBox.Intersects(pl.Hitbox))
                    {
                        hitPlayers.Add(p);

                        Vector2 knockDir = DirectionVector(Angle);

                        pl.Hurt(PlayerDeathReason.LegacyDefault(), damage, Math.Sign(knockDir.X));
                        pl.velocity += knockDir * 6f;
                    }
                }

                for (int n = 0; n < Main.maxNPCs; n++)
                {
                    NPC npc = Main.npc[n];

                    if (!npc.active || npc.friendly || hitNPCs.Contains(n))
                        continue;

                    if (sampleBox.Intersects(npc.Hitbox))
                    {
                        hitNPCs.Add(n);

                        Vector2 knockDir = DirectionVector(Angle);

                        npc.StrikeNPC(new NPC.HitInfo
                        {
                            Damage = damage,
                            Knockback = 6f,
                            HitDirection = Math.Sign(knockDir.X)
                        });
                    }
                }
            }
        }

        private void SpawnFallingLeaves(int heightPixels)
        {
            if (Main.rand.NextFloat() > AngularVelocity * 0.35f)
                return;

            Vector2 tip = Projectile.Center + DirectionVector(Angle) * heightPixels;

            int d = Dust.NewDust(
                tip - new Vector2(8, 8),
                16,
                16,
                DustID.WoodFurniture,
                0f,
                0f,
                100,
                default,
                0.9f);

            Main.dust[d].color = new Color(70, 130, 60);
            Main.dust[d].noGravity = false;
            Main.dust[d].velocity = new Vector2(
                Main.rand.NextFloat(-1.5f, 1.5f),
                Main.rand.NextFloat(-1f, 0.5f));
        }

        private bool HasPivotSupport()
        {
            int px = (int)Math.Floor(Projectile.Center.X / 16f);
            int py = (int)Math.Floor(Projectile.Center.Y / 16f);

            return IsSupportTile(px, py);
        }

        private bool HasSupport(float angle, int heightPixels)
        {
            const int checkPoints = 4;

            for (int i = 1; i <= checkPoints; i++)
            {
                float dist = heightPixels * (i / (float)checkPoints);

                Vector2 point = Projectile.Center + DirectionVector(angle) * dist;

                int px = (int)Math.Floor(point.X / 16f);
                int py = (int)Math.Floor(point.Y / 16f);

                if (IsSupportTile(px, py) || IsSupportTile(px, py + 1))
                    return true;
            }

            Vector2 tip = Projectile.Center + DirectionVector(angle) * heightPixels;

            int tipX = (int)Math.Floor(tip.X / 16f);
            int tipY = (int)Math.Floor(tip.Y / 16f);

            return IsSupportTile(tipX, tipY) || IsSupportTile(tipX, tipY + 1);
        }

        private bool IsSupportTile(int x, int y)
        {
            if (!WorldGen.InWorld(x, y))
                return false;

            Tile t = Main.tile[x, y];

            if (!t.HasTile)
                return false;

            return Main.tileSolid[t.TileType]
                || t.TileType == TileID.Trees
                || t.TileType == TileID.PalmTree;
        }

        private void SpawnChopParticles(Vector2 hitPosition)
        {
            for (int n = 0; n < 5; n++)
            {
                int d = Dust.NewDust(
                    hitPosition - new Vector2(8, 8),
                    16,
                    16,
                    DustID.WoodFurniture,
                    0f,
                    0f,
                    100,
                    default,
                    1f);

                Main.dust[d].velocity = new Vector2(
                    Main.rand.NextFloat(-2f, 2f),
                    Main.rand.NextFloat(-2f, 0f));
            }
        }

        private void PlayRandomFallSound()
        {
            if (fallSoundPlayed)
                return;

            try
            {
                int soundIndex = Main.rand.Next(TreeFallSounds.Length);
                float volume = MathHelper.Clamp(0.5f + TreeHeightTiles / 30f, 0.5f, 1.0f);

                SoundEngine.PlaySound(TreeFallSounds[soundIndex].WithVolumeScale(volume), Projectile.Center);

                fallSoundPlayed = true;
            }
            catch
            {
                fallSoundPlayed = true;
            }
        }

        private void PlayImpactSound()
        {
            try
            {
                float volume = MathHelper.Clamp(0.6f + TreeHeightTiles / 25f, 0.6f, 1.0f);
                SoundEngine.PlaySound(TreeImpactSound.WithVolumeScale(volume), Projectile.Center);
            }
            catch
            {
                SoundEngine.PlaySound(SoundID.Item1, Projectile.Center);
            }
        }

        public override void OnKill(int timeLeft)
        {
            treeTexture?.Dispose();
            treeTexture = null;

            FallenTreesWorld.RemoveTreeByProjectileId(Projectile.whoAmI);
            RemoveFrames(Projectile.whoAmI);
        }

        public override bool PreDraw(ref Color lightColor)
        {
            if (treeTexture == null)
                return false;

            Color drawColor = Lighting.GetColor(
                (int)(Projectile.Center.X / 16f),
                (int)(Projectile.Center.Y / 16f));

            float alpha = 1f;

            if (CurrentPhase == Phase.Shattering)
            {
                alpha = 1f - (ShatterTimer / (float)ShatterDuration);

                if (alpha <= 0f)
                    return false;

                drawColor *= alpha;

                // Если есть осколки — рисуем их вместо целого ствола.
                if (shatterFragments != null && shatterFragments.Length > 0)
                {
                    Vector2 trunkDir = DirectionVector(Angle);

                    foreach (var frag in shatterFragments)
                    {
                        Vector2 fragCenter =
                            Projectile.Center +
                            trunkDir * frag.Distance +
                            frag.Offset -
                            Main.screenPosition;

                        Vector2 fragOrigin = new Vector2(
                            frag.Source.Width * 0.5f,
                            frag.Source.Height * 0.5f);

                        Main.EntitySpriteDraw(
                            treeTexture,
                            fragCenter,
                            frag.Source,
                            drawColor,
                            Angle * Direction + frag.Rotation,
                            fragOrigin,
                            1f,
                            SpriteEffects.None,
                            0);
                    }

                    return false;
                }
            }

            // Запасной вариант: рисуем целый ствол, если осколки по какой-то причине не созданы.
            Vector2 drawPos = Projectile.Center - Main.screenPosition;
            Vector2 origin = new Vector2(pivotX, pivotY);
            float rotation = Angle * Direction;

            Main.EntitySpriteDraw(
                treeTexture,
                drawPos,
                null,
                drawColor,
                rotation,
                origin,
                1f,
                SpriteEffects.None,
                0);

            return false;
        }

        public override void PostDraw(Color lightColor)
        {
            if (!DebugDraw)
                return;

            if (CurrentPhase != Phase.Falling && CurrentPhase != Phase.Bounce)
                return;

            int heightPixels = TreeHeightTiles * 16;
            int physicalHeight = PhysicalHeightPixels;

            Vector2 screenOffset = Main.screenPosition;

            Vector2 pivotScreen = Projectile.Center - screenOffset;
            Vector2 tipScreen = Projectile.Center + DirectionVector(Angle) * physicalHeight - screenOffset;

            Main.spriteBatch.DrawLine(pivotScreen, tipScreen, Color.Green, 2);

            Vector2 visualTipScreen = Projectile.Center + DirectionVector(Angle) * heightPixels - screenOffset;
            Main.spriteBatch.DrawLine(tipScreen, visualTipScreen, Color.Red, 2);

            const int checkPoints = 4;

            for (int i = 1; i <= checkPoints; i++)
            {
                float dist = physicalHeight * (i / (float)(checkPoints + 1));
                Vector2 point = Projectile.Center + DirectionVector(Angle) * dist - screenOffset;

                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    new Rectangle((int)point.X - 3, (int)point.Y - 3, 6, 6),
                    Color.Yellow);
            }

            Main.spriteBatch.Draw(
                TextureAssets.MagicPixel.Value,
                new Rectangle((int)pivotScreen.X - 4, (int)pivotScreen.Y - 4, 8, 8),
                Color.Blue);

            Main.spriteBatch.Draw(
                TextureAssets.MagicPixel.Value,
                new Rectangle((int)tipScreen.X - 4, (int)tipScreen.Y - 4, 8, 8),
                Color.Green);

            Main.spriteBatch.Draw(
                TextureAssets.MagicPixel.Value,
                new Rectangle((int)visualTipScreen.X - 4, (int)visualTipScreen.Y - 4, 8, 8),
                Color.Red);
        }
    }
}
