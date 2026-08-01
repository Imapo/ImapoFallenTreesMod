using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace FallingTrees.Content.Projectiles
{
    public class FallingTreeProjectile : ModProjectile
    {
        private enum Phase { Warmup, Falling, Bounce, Landed }

        private int TreeHeightTiles => (int)Projectile.ai[0];
        private int Direction => (int)Projectile.ai[1];

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
        private const int FallTicks = 100;
        private const int BounceTicks = 26;
        private const float BounceAmplitude = 0.11f;

        private readonly HashSet<int> hitPlayers = new HashSet<int>();
        private readonly HashSet<int> hitNPCs = new HashSet<int>();

        public override string Texture => "FallingTrees/Content/Projectiles/FallingTreeProjectile";

        public void Init(int heightTiles, int direction, Texture2D composite, int pivotXIn, int pivotYIn)
        {
            Projectile.ai[0] = heightTiles;
            Projectile.ai[1] = direction;
            CurrentPhase = Phase.Warmup;
            PhaseTimer = 0f;
            ChopCooldown = 0f;
            Angle = 0f;
            AngularVelocity = 0f;
            treeTexture = composite;
            pivotX = pivotXIn;
            pivotY = pivotYIn;
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
                case Phase.Landed: UpdateLanded(); break; // ИСПРАВЛЕНО: больше не вызывает Projectile.Kill()
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
                AngularVelocity = 0f;
            }
        }

        private float restAngle;

        private void UpdateFalling()
        {
            int heightPixels = TreeHeightTiles * 16;
            PhaseTimer++;
            float t = MathHelper.Clamp(PhaseTimer / FallTicks, 0f, 1f);
            float eased = t * t * t;
            float targetAngle = eased * MathHelper.PiOver2;

            if (WouldTipHitObstacle(targetAngle, heightPixels))
            {
                StartBounce(Angle);
                return;
            }

            AngularVelocity = MathHelper.Clamp(3f * t * t, 0f, 1f);
            Angle = targetAngle;
            DamageEntitiesAlongTrunk(heightPixels);
            SpawnFallingLeaves(heightPixels);

            if (t >= 1f)
                StartBounce(MathHelper.PiOver2);
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
                Angle = restAngle; // Дерево остаётся под углом, на котором остановилось!
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
            return Main.tileSolid[t.TileType] || t.TileType == TileID.Trees;
        }

        private void DamageEntitiesAlongTrunk(int heightPixels)
        {
            const int samples = 6;
            int damage = (int)MathHelper.Lerp(8, 60, AngularVelocity);
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

        private void UpdateLanded()
        {
            Projectile.timeLeft = 2; // Бессмертие, пока не срубят

            Player player = Main.LocalPlayer;
            if (player == null || !player.active || player.dead)
                return;

            // Проверяем рубку: игрок держит топор и замахивается (itemAnimation > 0)
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
            int woodAmount = TreeHeightTiles;
            var source = new EntitySource_Misc("FallenTreeChop");
            Item.NewItem(source, (int)Projectile.Center.X, (int)Projectile.Center.Y, 16, 16, ItemID.Wood, woodAmount);

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
            
            // ИСПРАВЛЕНО: убрано условие CurrentPhase == Phase.Landed, чтобы дерево рисовалось, когда лежит
            
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
