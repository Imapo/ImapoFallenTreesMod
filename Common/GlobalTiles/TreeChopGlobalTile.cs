using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using ImapoFallingTrees.Content.Projectiles;

namespace ImapoFallingTrees.Common.GlobalTiles
{
    public class TreeChopGlobalTile : GlobalTile
    {
        // ID тайла ветвей деревьев в ванильной Terraria = 6
        // Используем числовое значение, так как TileID.TreeBranch отсутствует в API tModLoader 1.4.4.9
        private const ushort TreeBranchTileId = 6;

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

        private static readonly Dictionary<(int x, int rootY), List<CachedFrame>> cachedColumns = new();

        public override bool CanKillTile(int i, int j, int type, ref bool blockDamaged)
        {
            if (type == TileID.Trees && !Main.dedServ && !WorldGen.generatingWorld)
            {
                CacheColumn(i, j);
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

            List<TrunkFrameData> upperFrames = GetUpperPartFromCacheOrScan(i, j);
            if (upperFrames.Count == 0)
                return;

            lastHandledTile = new Point16((short)i, (short)j);
            lastHandledFrame = Main.GameUpdateCount;

            isProcessing = true;
            try
            {
                SpawnFallingTree(i, j, upperFrames);
                noItem = true;
                
                // Удаляем тайлы по их точным координатам Y, игнорируя ветви
                RemoveAbove(i, j, upperFrames);
            }
            catch (Exception)
            {
            }
            finally
            {
                isProcessing = false;
                cachedColumns.Remove((i, FindRoot(i, j)));
            }
        }

        private void CacheColumn(int i, int j)
        {
            int rootY = FindRoot(i, j);
            int topY = FindTop(i, j);

            var list = new List<CachedFrame>();
            for (int y = rootY; y >= topY; y--)
            {
                Tile t = Main.tile[i, y];
                if (!t.HasTile)
                    break;

                // === ИГНОРИРУЕМ ВЕТВИ (ID = 6) ===
                if (t.TileType == TreeBranchTileId)
                    continue;

                if (t.TileType != TileID.Trees)
                    break;

                Color color = t.TileColor > 0 ? WorldGen.paintColor(t.TileColor) : Color.White;
                list.Add(new CachedFrame { Y = y, FrameX = t.TileFrameX, FrameY = t.TileFrameY, Color = color });
            }

            if (list.Count > 0)
            {
                cachedColumns[(i, rootY)] = list;
                if (cachedColumns.Count > 400)
                    cachedColumns.Clear();
            }
        }

        private List<TrunkFrameData> GetUpperPartFromCacheOrScan(int i, int j)
        {
            int rootY = FindRoot(i, j);
            if (cachedColumns.TryGetValue((i, rootY), out List<CachedFrame> cached) && cached.Count > 0)
            {
                var result = new List<TrunkFrameData>();
                foreach (var f in cached)
                {
                    if (f.Y <= j)
                    {
                        result.Add(new TrunkFrameData 
                        { 
                            Y = f.Y, 
                            FrameX = f.FrameX, 
                            FrameY = f.FrameY, 
                            Color = f.Color 
                        });
                    }
                }
                if (result.Count > 0)
                    return result;
            }

            // Фолбэк: живое сканирование
            var frames = new List<TrunkFrameData>();
            int y = j;
            while (y >= 0)
            {
                Tile t = Main.tile[i, y];
                if (!t.HasTile)
                    break;

                // === ИГНОРИРУЕМ ВЕТВИ ===
                if (t.TileType == TreeBranchTileId)
                {
                    y--;
                    continue;
                }

                if (t.TileType != TileID.Trees)
                    break;

                Color color = t.TileColor > 0 ? WorldGen.paintColor(t.TileColor) : Color.White;
                frames.Add(new TrunkFrameData { Y = y, FrameX = t.TileFrameX, FrameY = t.TileFrameY, Color = color });
                y--;
            }
            return frames;
        }

        private void RemoveAbove(int i, int j, List<TrunkFrameData> upperFrames)
        {
            // Проходим по всем собранным фреймам, начиная с 1 (пропускаем сам тайл j, его удалит ваниль)
            for (int k = 1; k < upperFrames.Count; k++)
            {
                int y = upperFrames[k].Y; // Берем точную координату Y из кэша
                
                if (Main.tile[i, y].HasTile && Main.tile[i, y].TileType == TileID.Trees)
                {
                    WorldGen.KillTile(i, y, fail: false, effectOnly: false, noItem: true);
                }
            }
        }

        private int FindRoot(int i, int j)
        {
            int y = j;
            while (WorldGen.InWorld(i, y + 1))
            {
                Tile t = Main.tile[i, y + 1];
                // Ищем корень, учитывая, что по пути могут быть ветви
                if (t.HasTile && (t.TileType == TileID.Trees || t.TileType == TreeBranchTileId))
                    y++;
                else
                    break;
            }
            return y;
        }

        private int FindTop(int i, int j)
        {
            int y = j;
            while (WorldGen.InWorld(i, y - 1))
            {
                Tile t = Main.tile[i, y - 1];
                // Ищем верхушку, учитывая, что по пути могут быть ветви
                if (t.HasTile && (t.TileType == TileID.Trees || t.TileType == TreeBranchTileId))
                    y--;
                else
                    break;
            }
            return y;
        }

        private void SpawnFallingTree(int i, int j, List<TrunkFrameData> frames)
        {
            int direction = ChooseFallDirectionByWind();

            // Крона убрана, создаем только чистый слепок ствола
            Texture2D composite = TreeTextureBuilder.Build(frames, out int pivotX, out int pivotY);

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

    /// <summary>
    /// Строитель текстур создает ТОЛЬКО ствол, без кроны.
    /// Это гарантирует отсутствие визуальных багов и максимальную стабильность.
    /// </summary>
    public static class TreeTextureBuilder
    {
        public static Texture2D Build(List<TrunkFrameData> trunkFrames, out int pivotX, out int pivotY)
        {
            GraphicsDevice device = Main.instance.GraphicsDevice;
            Texture2D trunkSheet = TextureAssets.Tile[TileID.Trees].Value;

            int width = 16;
            int height = trunkFrames.Count * 16;

            var target = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None);
            RenderTargetBinding[] oldTargets = device.GetRenderTargets();

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);

            var sb = new SpriteBatch(device);
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone, null, Matrix.Identity);

            for (int k = 0; k < trunkFrames.Count; k++)
            {
                var f = trunkFrames[k];
                var src = new Rectangle(f.FrameX, f.FrameY, 16, 16);
                
                // Рисуем снизу вверх: k=0 (место удара) находится в самом низу текстуры
                int y = height - (k + 1) * 16;
                sb.Draw(trunkSheet, new Vector2(0, y), src, f.Color);
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
        public int Y;          
        public int FrameX;
        public int FrameY;
        public Color Color;
    }
}
