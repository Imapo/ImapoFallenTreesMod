using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using FallingTrees.Content.Projectiles;

namespace FallingTrees.Common.GlobalTiles
{
    public class TreeChopGlobalTile : GlobalTile
    {
        private static Point16 lastHandledTile = new Point16(-1, -1);
        private static uint lastHandledFrame;

        public override bool CanKillTile(int i, int j, int type, ref bool blockDamaged)
        {
            if (type != TileID.Trees)
                return true;

            Tile tile = Main.tile[i, j];
            if (!tile.HasTile)
                return true;

            // === КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ ===
            // Если это не реальный замах топором, возвращаем true, чтобы ваниль 
            // могла обработать наведение курсора (подсветку), но не ломала дерево.
            if (!IsRealAxeChop(i, j))
                return true;

            if (lastHandledTile.X == i && lastHandledTile.Y == j && lastHandledFrame == Main.GameUpdateCount)
                return true;

            List<TrunkFrameData> frames = CaptureTrunkFrames(i, j);
            if (frames.Count == 0)
                return true;

            lastHandledTile = new Point16((short)i, (short)j);
            lastHandledFrame = Main.GameUpdateCount;

            SpawnFallingTree(i, j, frames);
            RemoveTrunk(i, j, frames.Count);

            return false; // Отменяем ванильное разрушение, мы всё сделали сами
        }

        private bool IsRealAxeChop(int i, int j)
        {
            Player player = Main.LocalPlayer;
            if (player == null || !player.active || player.dead)
                return false;

            Item held = player.HeldItem;
            if (held == null || held.axe <= 0)
                return false;

            // itemAnimation > 0 означает, что игрок находится в анимации замаха.
            // При простом наведении смарт-курсора itemAnimation всегда равен 0.
            if (player.itemAnimation <= 0)
                return false;

            Vector2 tileCenter = new Vector2(i * 16f + 8f, j * 16f + 8f);
            if (Vector2.Distance(player.Center, tileCenter) > 80f) // ~5 тайлов
                return false;

            return true;
        }

        private List<TrunkFrameData> CaptureTrunkFrames(int i, int j)
        {
            var frames = new List<TrunkFrameData>();
            int y = j;
            while (y >= 0)
            {
                Tile t = Main.tile[i, y];
                if (!t.HasTile || t.TileType != TileID.Trees)
                    break;

                Color color = t.TileColor > 0 ? WorldGen.paintColor(t.TileColor) : Color.White;
                frames.Add(new TrunkFrameData { FrameX = t.TileFrameX, FrameY = t.TileFrameY, Color = color });
                y--;
            }
            return frames;
        }

        private void RemoveTrunk(int i, int j, int height)
        {
            for (int y = j; y > j - height; y--)
            {
                if (Main.tile[i, y].HasTile && Main.tile[i, y].TileType == TileID.Trees)
                {
                    WorldGen.KillTile(i, y, noItem: true);
                }
            }
        }

        private void SpawnFallingTree(int i, int j, List<TrunkFrameData> frames)
        {
            int direction = ChooseFallDirectionByWind();
            int biomeStyle = DetectBiomeStyle(i, j);

            Texture2D composite = TreeTextureBuilder.Build(frames, biomeStyle, out int pivotX, out int pivotY);
            Vector2 basePos = new Vector2(i * 16f + 8f, j * 16f + 16f);

            int type = ModContent.ProjectileType<FallingTreeProjectile>();
            int proj = Projectile.NewProjectile(null, basePos.X, basePos.Y, 0f, 0f, type, 0, 0f, Main.myPlayer);

            if (Main.projectile[proj].ModProjectile is FallingTreeProjectile ft)
            {
                ft.Init(frames.Count, direction, composite, pivotX, pivotY);
            }
        }

        private int ChooseFallDirectionByWind()
        {
            float wind = Main.windSpeedCurrent;
            const float windDeadZone = 0.02f;
            if (wind > windDeadZone) return 1;
            if (wind < -windDeadZone) return -1;
            return Main.rand.NextBool() ? -1 : 1;
        }

        private int DetectBiomeStyle(int i, int j)
        {
            const int radius = 24;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = i + dx;
                if (!WorldGen.InWorld(x, j)) continue;

                for (int dy = -6; dy <= 6; dy++)
                {
                    int y = j + dy;
                    if (!WorldGen.InWorld(x, y)) continue;

                    ushort t = Main.tile[x, y].TileType;
                    if (!Main.tile[x, y].HasTile) continue;

                    if (t == TileID.Ebonstone || t == TileID.CorruptGrass) return 1;
                    if (t == TileID.Crimstone || t == TileID.CrimsonGrass) return 2;
                    if (t == TileID.Pearlstone || t == TileID.HallowedGrass) return 3;
                    if (t == TileID.JungleGrass) return 4;
                    if (t == TileID.SnowBlock || t == TileID.IceBlock) return 5;
                }
            }
            return 0;
        }
    }

    public static class TreeTextureBuilder
    {
        private const int CanopyFrameWidth = 80;
        private const int CanopyFrameHeight = 80;
        private const int CanopyFrameGap = 2;
        private const int CanopyOverlapWithTrunk = 16;
        private const int MinTopFrameY = 198;
        private const int MinTopFrameX = 22;

        public static Texture2D Build(List<TrunkFrameData> trunkFrames, int biomeStyle, out int pivotX, out int pivotY)
        {
            GraphicsDevice device = Main.instance.GraphicsDevice;
            Texture2D trunkSheet = TextureAssets.Tile[TileID.Trees].Value;

            TrunkFrameData topPiece = trunkFrames[trunkFrames.Count - 1];
            bool hasCanopy = topPiece.FrameY >= MinTopFrameY && topPiece.FrameX >= MinTopFrameX;

            Texture2D canopyStrip = null;
            Rectangle canopySource = Rectangle.Empty;

            if (hasCanopy && TextureAssets.TreeTop != null && TextureAssets.TreeTop.Length > 0)
            {
                int styleIdx = System.Math.Clamp(biomeStyle, 0, TextureAssets.TreeTop.Length - 1);
                canopyStrip = TextureAssets.TreeTop[styleIdx]?.Value;

                if (canopyStrip != null)
                {
                    int variant = (topPiece.FrameX / 22) - 1;
                    int maxVariant = System.Math.Max(0, canopyStrip.Width / (CanopyFrameWidth + CanopyFrameGap) - 1);
                    variant = System.Math.Clamp(variant, 0, maxVariant);
                    canopySource = new Rectangle(variant * (CanopyFrameWidth + CanopyFrameGap), 0, CanopyFrameWidth, CanopyFrameHeight);
                }
            }

            int trunkPixelHeight = trunkFrames.Count * 16;
            int canopyExtraHeight = canopyStrip != null ? (CanopyFrameHeight - CanopyOverlapWithTrunk) : 0;

            int width = System.Math.Max(16, canopyStrip != null ? CanopyFrameWidth : 16);
            int height = trunkPixelHeight + canopyExtraHeight;

            var target = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None);
            RenderTargetBinding[] oldTargets = device.GetRenderTargets();

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);

            var sb = new SpriteBatch(device);
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone, null, Matrix.Identity);

            int centerX = (width - 16) / 2;
            for (int k = 0; k < trunkFrames.Count; k++)
            {
                var f = trunkFrames[k];
                var src = new Rectangle(f.FrameX, f.FrameY, 16, 16);
                int y = height - (k + 1) * 16;
                sb.Draw(trunkSheet, new Vector2(centerX, y), src, f.Color);
            }

            if (canopyStrip != null)
            {
                float canopyX = centerX - (CanopyFrameWidth - 16) / 2f;
                sb.Draw(canopyStrip, new Vector2(canopyX, 0), canopySource, Color.White);
            }

            sb.End();
            sb.Dispose();
            device.SetRenderTargets(oldTargets);

            pivotX = width / 2;
            pivotY = height;

            return target;
        }
    }

    public struct TrunkFrameData
    {
        public int FrameX;
        public int FrameY;
        public Color Color;
    }
}
