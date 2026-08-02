using System;
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
        private static bool isProcessing = false;

        private struct CachedFrame
        {
            public int Y;
            public int FrameX;
            public int FrameY;
            public Color Color;
        }

        private struct CachedTreeData
        {
            public List<CachedFrame> Frames;
            public int TreeTopIndex; // Индекс текстуры кроны в TextureAssets.TreeTop
        }

        private static readonly Dictionary<(int x, int rootY), CachedTreeData> cachedTrees = new();

        public override bool CanKillTile(int i, int j, int type, ref bool blockDamaged)
        {
            if (type == TileID.Trees && !Main.dedServ && !WorldGen.generatingWorld)
            {
                CacheTreeData(i, j);
            }
            return true;
        }

        public override void KillTile(int i, int j, int type, ref bool fail, ref bool effectOnly, ref bool noItem)
        {
            if (type != TileID.Trees)
                return;

            if (fail || effectOnly)
                return;

            if (WorldGen.generatingWorld || Main.dedServ)
                return;

            if (isProcessing)
                return;

            if (lastHandledTile.X == i && lastHandledTile.Y == j && lastHandledFrame == Main.GameUpdateCount)
                return;

            List<TrunkFrameData> upperFrames;
            int treeTopIndex;

            int rootY = FindRoot(i, j);
            if (cachedTrees.TryGetValue((i, rootY), out CachedTreeData cached) && cached.Frames.Count > 0)
            {
                upperFrames = GetUpperPartFromCache(cached.Frames, j);
                treeTopIndex = cached.TreeTopIndex; // Используем кэшированный индекс
            }
            else
            {
                upperFrames = ScanUpperFrames(i, j);
                treeTopIndex = 0; // Фолбэк: обычное дерево
            }

            if (upperFrames.Count == 0)
                return;

            lastHandledTile = new Point16((short)i, (short)j);
            lastHandledFrame = Main.GameUpdateCount;

            isProcessing = true;
            try
            {
                SpawnFallingTree(i, j, upperFrames, treeTopIndex);
                noItem = true;
                RemoveAbove(i, j, upperFrames.Count);
            }
            catch (Exception)
            {
            }
            finally
            {
                isProcessing = false;
                cachedTrees.Remove((i, rootY));
            }
        }

        /// <summary>
        /// Кэширует данные дерева в CanKillTile, когда дерево ещё целое.
        /// Определяет стиль дерева по блокам под корнем и сохраняет индекс кроны.
        /// </summary>
        private void CacheTreeData(int i, int j)
        {
            int rootY = FindRoot(i, j);
            int topY = FindTop(i, j);

            var frames = new List<CachedFrame>();
            for (int y = rootY; y >= topY; y--)
            {
                Tile t = Main.tile[i, y];
                if (!t.HasTile || t.TileType != TileID.Trees)
                    break;

                Color color = t.TileColor > 0 ? WorldGen.paintColor(t.TileColor) : Color.White;
                frames.Add(new CachedFrame { Y = y, FrameX = t.TileFrameX, FrameY = t.TileFrameY, Color = color });
            }

            if (frames.Count == 0)
                return;

            // Определяем стиль дерева ПОКА ДЕРЕВО ЦЕЛОЕ
            int treeStyle = GetTreeStyleFromRoot(i, rootY);
            int treeTopIndex = GetTreeTopIndex(treeStyle);

            cachedTrees[(i, rootY)] = new CachedTreeData { Frames = frames, TreeTopIndex = treeTopIndex };

            if (cachedTrees.Count > 400)
                cachedTrees.Clear();
        }

        /// <summary>
        /// Определяет стиль дерева по блокам под корнем.
        /// </summary>
        private int GetTreeStyleFromRoot(int i, int rootY)
        {
            for (int dy = 1; dy <= 3; dy++)
            {
                int y = rootY + dy;
                if (!WorldGen.InWorld(i, y)) continue;

                Tile below = Main.tile[i, y];
                if (!below.HasTile) continue;

                ushort t = below.TileType;

                if (t == TileID.Ebonstone || t == TileID.CorruptGrass)
                    return 1;
                if (t == TileID.Crimstone || t == TileID.CrimsonGrass)
                    return 2;
                if (t == TileID.Pearlstone || t == TileID.HallowedGrass)
                    return 3;
                if (t == TileID.JungleGrass || t == TileID.Mud)
                    return 4;
                if (t == TileID.SnowBlock || t == TileID.IceBlock)
                    return 5;
            }

            return 0;
        }

        /// <summary>
        /// Преобразует treeStyle в индекс массива TreeTop.
        /// Основано на скриншоте TreeTop_*.png
        /// </summary>
        private int GetTreeTopIndex(int treeStyle)
        {
            switch (treeStyle)
            {
                case 0: return 0;   // обычное дерево
                case 1: return 1;   // Corruption (фиолетовая)
                case 2: return 5;   // Crimson (красная)
                case 3: return 3;   // Hallow (разноцветная)
                case 4: return 14;  // Jungle (грибная)
                case 5: return 12;  // Snow (заснеженная)
                default: return 0;
            }
        }

        private List<TrunkFrameData> GetUpperPartFromCache(List<CachedFrame> cached, int j)
        {
            var result = new List<TrunkFrameData>();
            foreach (var f in cached)
            {
                if (f.Y <= j)
                    result.Add(new TrunkFrameData { FrameX = f.FrameX, FrameY = f.FrameY, Color = f.Color });
            }
            return result;
        }

        private List<TrunkFrameData> ScanUpperFrames(int i, int j)
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

        private int FindRoot(int i, int j)
        {
            int y = j;
            while (WorldGen.InWorld(i, y + 1) && Main.tile[i, y + 1].HasTile && Main.tile[i, y + 1].TileType == TileID.Trees)
                y++;
            return y;
        }

        private int FindTop(int i, int j)
        {
            int y = j;
            while (WorldGen.InWorld(i, y - 1) && Main.tile[i, y - 1].HasTile && Main.tile[i, y - 1].TileType == TileID.Trees)
                y--;
            return y;
        }

        private void RemoveAbove(int i, int j, int upperHeight)
        {
            for (int y = j - 1; y >= j - upperHeight + 1; y--)
            {
                if (Main.tile[i, y].HasTile && Main.tile[i, y].TileType == TileID.Trees)
                {
                    WorldGen.KillTile(i, y, fail: false, effectOnly: false, noItem: true);
                }
            }
        }

        private void SpawnFallingTree(int i, int j, List<TrunkFrameData> frames, int treeTopIndex)
        {
            int direction = ChooseFallDirectionByWind();

            Texture2D composite = TreeTextureBuilder.Build(frames, treeTopIndex, out int pivotX, out int pivotY);

            int type = ModContent.ProjectileType<FallingTreeProjectile>();
            int proj = Projectile.NewProjectile(null, 0f, 0f, 0f, 0f, type, 0, 0f, Main.myPlayer);

            if (Main.projectile[proj].ModProjectile is FallingTreeProjectile ft)
            {
                ft.Init(frames.Count, direction, composite, pivotX, pivotY);
                Main.projectile[proj].Center = new Vector2(i * 16f + 8f, j * 16f + 16f);
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
    }

    public static class TreeTextureBuilder
    {
        private const int CanopyFrameWidth = 80;
        private const int CanopyFrameHeight = 80;
        private const int CanopyFrameGap = 2;
        private const int CanopyOverlapWithTrunk = 16;
        private const int MinTopFrameY = 198;
        private const int MinTopFrameX = 22;

        public static Texture2D Build(List<TrunkFrameData> trunkFrames, int treeTopIndex, out int pivotX, out int pivotY)
        {
            GraphicsDevice device = Main.instance.GraphicsDevice;
            Texture2D trunkSheet = TextureAssets.Tile[TileID.Trees].Value;

            TrunkFrameData topPiece = trunkFrames[trunkFrames.Count - 1];
            bool hasCanopy = topPiece.FrameY >= MinTopFrameY && topPiece.FrameX >= MinTopFrameX;

            Texture2D canopyStrip = null;
            Rectangle canopySource = Rectangle.Empty;

            if (hasCanopy && TextureAssets.TreeTop != null && treeTopIndex >= 0 && treeTopIndex < TextureAssets.TreeTop.Length)
            {
                canopyStrip = TextureAssets.TreeTop[treeTopIndex]?.Value;

                if (canopyStrip != null)
                {
                    int variant = (topPiece.FrameX / 22) - 1;
                    int maxVariant = Math.Max(0, canopyStrip.Width / (CanopyFrameWidth + CanopyFrameGap) - 1);
                    variant = Math.Clamp(variant, 0, maxVariant);

                    canopySource = new Rectangle(variant * (CanopyFrameWidth + CanopyFrameGap), 0, CanopyFrameWidth, CanopyFrameHeight);
                }
            }

            int trunkPixelHeight = trunkFrames.Count * 16;
            int canopyExtraHeight = canopyStrip != null ? (CanopyFrameHeight - CanopyOverlapWithTrunk) : 0;

            int width = Math.Max(16, canopyStrip != null ? CanopyFrameWidth : 16);
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
                float canopyX = centerX - 32f;
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
