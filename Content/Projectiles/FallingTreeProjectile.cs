using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace ImapoFallingTrees.Content.Projectiles
{
    public class FallingTreeProjectile : ModProjectile
    {
        private enum Phase { Warmup, Falling, Bounce, Landed }

        private int TreeHeightTiles => (int)Projectile.ai[0];
        private int Direction => (int)Projectile.ai[1];
        private int DropItemType => (int)Projectile.ai[2]; // Тип выпадающего предмета (Wood, PalmWood и т.д.)

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
        private float ChopCooldown
        {
            get => Projectile.localAI[2];
            set => Projectile.localAI[2] = value;
        }

        private float Angle;
        private float AngularVelocity;
        private Texture2D treeTexture;
        private int pivotX, pivotY;

        private const int WarmupTicks = 60;
        private const int BounceTicks = 26;
        private const float BounceAmplitude = 0.11f;
        private const float Gravity = 0.0008f;
        private const float MaxAngle = MathHelper.Pi * 0.9f;
        private const int SupportCheckInterval = 30;

        private static readonly SoundStyle[] TreeFallSounds = new SoundStyle[]
        {
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall1") { MaxInstances = 1 },
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall2") { MaxInstances = 1 },
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall3") { MaxInstances = 1 },
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall4") { MaxInstances = 1 },
            new SoundStyle("ImapoFallingTrees/Sounds/TreeFall5") { MaxInstances = 1 }
        };

        private static readonly SoundStyle TreeImpactSound = new SoundStyle("ImapoFallingTrees/Sounds/TreeImpact") { MaxInstances = 1 };

        private readonly HashSet<int> hitPlayers = new HashSet<int>();
        private readonly HashSet<int> hitNPCs = new HashSet<int>();

        private bool fallSoundPlayed = false;
        private bool impactSoundPlayed = false;

        public override string Texture => "ImapoFallingTrees/Content/Projectiles/FallingTreeProjectile";

        public void Init(int heightTiles, int direction, Texture2D composite, int pivotXIn, int pivotYIn, int dropItemType)
        {
            Projectile.ai[0] = heightTiles;
            Projectile.ai[1] = direction;
            Projectile.ai[2] = dropItemType; // Сохраняем тип дропа
            CurrentPhase = Phase.Warmup;
            PhaseTimer = 0f;
            ChopCooldown = 0f;
            Angle = 0f;
            AngularVelocity = 0f;
            treeTexture = composite;
            pivotX = pivotXIn;
            pivotY = pivotYIn;

            fallSoundPlayed = false;
            impactSoundPlayed = false;
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
                case Phase.Warmup: UpdateWarmup(); break;
                case Phase.Falling: UpdateFalling(); break;
                case Phase.Bounce: UpdateBounce(); break;
                case Phase.Landed: UpdateLanded(); break;
            }

            if (ChopCooldown > 0f)
                ChopCooldown -= 1f;
        }

        private void UpdateWarmup()
        {
            PhaseTimer++;
            float progress = PhaseTimer / WarmupTicks;
            float amplitude = MathHelper.Lerp(0.01f, 0.045f, progress);
            Angle = (float)Math.Sin(PhaseTimer * 0.16f) * amplitude;
            if (PhaseTimer % 20 == 0)
                Dust.NewDust(Projectile.position, 16, 16, DustID.WoodFurniture, 0f, -1f);

            if (PhaseTimer >= WarmupTicks)
            {
                CurrentPhase = Phase.Falling;
                PhaseTimer = 0f;
                Angle = 0f;
                AngularVelocity = 0.001f;
                PlayRandomFallSound();
            }
        }

        private float restAngle;

        private void UpdateFalling()
        {
            int heightPixels = TreeHeightTiles * 16;

            AngularVelocity += Gravity;
            Angle += AngularVelocity;

            if (WouldTipHitObstacle(Angle, heightPixels))
            {
                StartBounce(Angle);
                return;
            }

            if (Angle > MathHelper.PiOver2 && WouldTrunkHitGround(Angle, heightPixels))
            {
                StartBounce(Angle);
                return;
            }

            if (Angle >= MaxAngle)
            {
                StartBounce(MaxAngle);
                return;
            }

            DamageEntitiesAlongTrunk(heightPixels);
            SpawnFallingLeaves(heightPixels);
        }

        private bool WouldTrunkHitGround(float angle, int heightPixels)
        {
            const int checkPoints = 5;
            for (int i = 1; i <= checkPoints; i++)
            {
                float dist = heightPixels * (i / (float)(checkPoints + 1));
                Vector2 point = Projectile.Center + DirectionVector(angle) * dist;

                int tileX = (int)(point.X / 16f);
                int tileY = (int)(point.Y / 16f);

                if (!WorldGen.InWorld(tileX, tileY))
                    continue;

                Tile t = Main.tile[tileX, tileY];
                if (t.HasTile && Main.tileSolid[t.TileType])
                    return true;
            }
            return false;
        }

        private void SpawnFallingLeaves(int heightPixels)
        {
            if (Main.rand.NextFloat() > AngularVelocity * 0.35f)
                return;
            Vector2 tip = Projectile.Center + DirectionVector(Angle) * heightPixels;
            int d = Dust.NewDust(tip - new Vector2(8, 8), 16, 16, DustID.WoodFurniture, 0f, 0f, 100, default, 0.9f);
            Main.dust[d].color = new Color(70, 130, 60);
            Main.dust[d].noGravity = false;
            Main.dust[d].velocity = new Vector2(Main.rand.NextFloat(-1.5f, 1.5f), Main.rand.NextFloat(-1f, 0.5f));
        }

        private void StartBounce(float landedAngle)
        {
            CurrentPhase = Phase.Bounce;
            PhaseTimer = 0f;
            restAngle = landedAngle;
            Angle = restAngle;
            Vector2 impactPoint = Projectile.Center + DirectionVector(restAngle) * (TreeHeightTiles * 16);

            if (!impactSoundPlayed)
            {
                PlayImpactSound();
                impactSoundPlayed = true;
            }

            for (int n = 0; n < 12; n++)
            {
                int d = Dust.NewDust(impactPoint - new Vector2(12, 12), 24, 24, DustID.WoodFurniture, 0f, -2f);
                Main.dust[d].velocity *= 1.5f;
            }
        }

        private void UpdateBounce()
        {
            PhaseTimer++;
            float t = PhaseTimer / BounceTicks;
            float damped = (float)(Math.Exp(-t * 5.0) * Math.Sin(t * MathHelper.TwoPi * 1.6));
            Angle = restAngle + damped * BounceAmplitude;

            if (PhaseTimer >= BounceTicks)
            {
                Angle = restAngle;
                LandTree();
            }
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
            int tileX = (int)(tip.X / 16f);
            int tileY = (int)(tip.Y / 16f);
            if (!WorldGen.InWorld(tileX, tileY)) return true;
            Tile t = Main.tile[tileX, tileY];
            if (!t.HasTile) return false;
            return Main.tileSolid[t.TileType] || t.TileType == TileID.Trees || t.TileType == TileID.PalmTree;
        }

        private void DamageEntitiesAlongTrunk(int heightPixels)
        {
            const int samples = 6;
            int damage = (int)MathHelper.Lerp(8, 60, AngularVelocity * 100f);
            for (int s = 1; s <= samples; s++)
            {
                float dist = heightPixels * (s / (float)samples);
                Vector2 point = Projectile.Center + DirectionVector(Angle) * dist;
                Rectangle sampleBox = new Rectangle((int)point.X - 8, (int)point.Y - 8, 16, 16);
                for (int p = 0; p < Main.maxPlayers; p++)
                {
                    Player pl = Main.player[p];
                    if (!pl.active || pl.dead || hitPlayers.Contains(p)) continue;
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
                    if (!npc.active || npc.friendly || hitNPCs.Contains(n)) continue;
                    if (sampleBox.Intersects(npc.Hitbox))
                    {
                        hitNPCs.Add(n);
                        Vector2 knockDir = DirectionVector(Angle);
                        npc.StrikeNPC(new NPC.HitInfo { Damage = damage, Knockback = 6f, HitDirection = Math.Sign(knockDir.X) });
                    }
                }
            }
        }

        private void LandTree()
        {
            CurrentPhase = Phase.Landed;
            PhaseTimer = 0f;
            Projectile.velocity = Vector2.Zero;
        }

        private bool HasSupport(float angle, int heightPixels)
        {
            const int checkPoints = 4;
            for (int i = 1; i <= checkPoints; i++)
            {
                float dist = heightPixels * (i / (float)checkPoints);
                Vector2 point = Projectile.Center + DirectionVector(angle) * dist;
                int px = (int)(point.X / 16f);
                int py = (int)(point.Y / 16f);

                if (!WorldGen.InWorld(px, py)) continue;
                Tile t = Main.tile[px, py];
                if (!t.HasTile) continue;

                if (Main.tileSolid[t.TileType] || t.TileType == TileID.Trees || t.TileType == TileID.PalmTree || t.TileType == 6)
                    return true;
            }

            Vector2 tip = Projectile.Center + DirectionVector(angle) * heightPixels;
            int tipX = (int)(tip.X / 16f);
            int tipY = (int)(tip.Y / 16f) + 1;

            if (WorldGen.InWorld(tipX, tipY))
            {
                Tile below = Main.tile[tipX, tipY];
                if (below.HasTile && Main.tileSolid[below.TileType])
                    return true;
            }

            return false;
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

            PlayRandomFallSound();

            for (int n = 0; n < 8; n++)
            {
                Dust.NewDust(Projectile.position, Projectile.width, Projectile.height,
                    DustID.WoodFurniture, 0f, -1f, 100, default, 1f);
            }
        }

        private void PlayRandomFallSound()
        {
            if (fallSoundPlayed) return;
            try
            {
                int soundIndex = Main.rand.Next(TreeFallSounds.Length);
                float volume = MathHelper.Clamp(0.5f + (TreeHeightTiles / 30f), 0.5f, 1.0f);
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
                float volume = MathHelper.Clamp(0.6f + (TreeHeightTiles / 25f), 0.6f, 1.0f);
                SoundEngine.PlaySound(TreeImpactSound.WithVolumeScale(volume), Projectile.Center);
            }
            catch
            {
                SoundEngine.PlaySound(SoundID.Item1, Projectile.Center);
            }
        }

        private void UpdateLanded()
        {
            Projectile.timeLeft = 2;

            PhaseTimer++;
            if (PhaseTimer >= SupportCheckInterval)
            {
                PhaseTimer = 0f;
                int heightPixels = TreeHeightTiles * 16;

                if (!HasSupport(Angle, heightPixels))
                {
                    ResumeFalling();
                    return;
                }
            }

            Player player = Main.LocalPlayer;
            if (player?.active != true || player.dead)
                return;

            if (ChopCooldown <= 0f && player.HeldItem != null && player.HeldItem.axe > 0 && player.itemAnimation > 0)
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
                        ChopDownedTree(player);
                    }
                }
            }
        }

        private void ChopDownedTree(Player player)
        {
            // Используем сохранённый тип дропа (Wood для деревьев, PalmWood для пальм)
            int dropType = DropItemType > 0 ? DropItemType : ItemID.Wood;
            int amount = TreeHeightTiles;

            var source = new EntitySource_Misc("FallenTreeChop");
            Item.NewItem(source, (int)Projectile.Center.X, (int)Projectile.Center.Y, 16, 16, dropType, amount);

            SoundEngine.PlaySound(SoundID.Dig, Projectile.Center);

            for (int n = 0; n < 14; n++)
            {
                int heightPixels = TreeHeightTiles * 16;
                Vector2 dir = DirectionVector(Angle);
                Vector2 tip = Projectile.Center + dir * heightPixels;
                float minX = Math.Min(Projectile.Center.X, tip.X) - 16;
                float maxX = Math.Max(Projectile.Center.X, tip.X) + 16;
                float minY = Math.Min(Projectile.Center.Y, tip.Y) - 16;
                float maxY = Math.Max(Projectile.Center.Y, tip.Y) + 16;

                int d = Dust.NewDust(new Vector2(minX, minY), (int)(maxX - minX), (int)(maxY - minY), DustID.WoodFurniture, 0f, 0f, 100, default, 1.1f);
                Main.dust[d].velocity *= 1.4f;
            }

            Projectile.Kill();
        }

        public override void OnKill(int timeLeft)
        {
            treeTexture?.Dispose();
            treeTexture = null;
        }

        public override bool PreDraw(ref Color lightColor)
        {
            if (treeTexture == null)
                return false;

            Vector2 drawPos = Projectile.Center - Main.screenPosition;
            Vector2 origin = new Vector2(pivotX, pivotY);
            float rotation = Angle * Direction;

            Main.EntitySpriteDraw(
                treeTexture,
                drawPos,
                null,
                Lighting.GetColor((int)(Projectile.Center.X / 16f), (int)(Projectile.Center.Y / 16f)),
                rotation,
                origin,
                1f,
                SpriteEffects.None,
                0
            );
            return false;
        }
    }
}
