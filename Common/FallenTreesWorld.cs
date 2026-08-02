using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using ImapoFallingTrees.Content.Projectiles;
using ImapoFallingTrees.Common.GlobalTiles;

namespace ImapoFallingTrees.Common
{
    public class FallenTreesWorld : ModSystem
    {
        public static List<FallenTreeData> FallenTrees = new List<FallenTreeData>();
        private static bool pendingRespawn = false; // Флаг для отложенного создания в главном потоке

        public class FallenTreeData
        {
            public int ProjectileId;
            public int RootX, RootY;
            public float Angle;
            public int Direction;
            public int DropItemType;
            public float ChopProgress;
            public int TreeHeightTiles;
            public List<FrameData> Frames;
        }

        public struct FrameData
        {
            public int FrameX;
            public int FrameY;
            public byte R, G, B, A;
        }

        public override void SaveWorldData(TagCompound tag)
        {
            var list = new List<TagCompound>();
            foreach (var tree in FallenTrees)
            {
                // Обновляем данные из активного проектайла перед сохранением
                if (tree.ProjectileId >= 0 && tree.ProjectileId < Main.maxProjectiles)
                {
                    var proj = Main.projectile[tree.ProjectileId];
                    if (proj.active && proj.ModProjectile is FallingTreeProjectile ft)
                    {
                        tree.Angle = ft.SavedAngle;
                        tree.ChopProgress = ft.SavedChopProgress;
                    }
                }

                var framesTag = new List<TagCompound>();
                foreach (var f in tree.Frames)
                {
                    framesTag.Add(new TagCompound
                    {
                        ["fx"] = f.FrameX,
                        ["fy"] = f.FrameY,
                        ["r"] = f.R,
                        ["g"] = f.G,
                        ["b"] = f.B,
                        ["a"] = f.A
                    });
                }

                list.Add(new TagCompound
                {
                    ["projId"] = tree.ProjectileId,
                    ["x"] = tree.RootX,
                    ["y"] = tree.RootY,
                    ["angle"] = tree.Angle,
                    ["dir"] = tree.Direction,
                    ["drop"] = tree.DropItemType,
                    ["chop"] = tree.ChopProgress,
                    ["height"] = tree.TreeHeightTiles,
                    ["frames"] = framesTag
                });
            }
            tag["fallenTrees"] = list;
        }

        public override void LoadWorldData(TagCompound tag)
        {
            FallenTrees.Clear();
            if (!tag.ContainsKey("fallenTrees")) return;

            var list = tag.GetList<TagCompound>("fallenTrees");
            foreach (var t in list)
            {
                var frames = new List<FrameData>();
                foreach (var ft in t.GetList<TagCompound>("frames"))
                {
                    frames.Add(new FrameData
                    {
                        FrameX = ft.GetInt("fx"),
                        FrameY = ft.GetInt("fy"),
                        R = ft.GetByte("r"),
                        G = ft.GetByte("g"),
                        B = ft.GetByte("b"),
                        A = ft.GetByte("a")
                    });
                }

                FallenTrees.Add(new FallenTreeData
                {
                    ProjectileId = t.GetInt("projId"),
                    RootX = t.GetInt("x"),
                    RootY = t.GetInt("y"),
                    Angle = t.GetFloat("angle"),
                    Direction = t.GetInt("dir"),
                    DropItemType = t.GetInt("drop"),
                    ChopProgress = t.GetFloat("chop"),
                    TreeHeightTiles = t.GetInt("height"),
                    Frames = frames
                });
            }
            
            // Устанавливаем флаг, что нужно создать проектайлы в главном потоке
            if (FallenTrees.Count > 0)
            {
                pendingRespawn = true;
            }
        }

        // Этот метод выполняется в ГЛАВНОМ потоке, здесь безопасно создавать текстуры
        public override void PostUpdateWorld()
        {
            if (pendingRespawn && FallenTrees.Count > 0)
            {
                // Используем ToList(), чтобы избежать ошибок изменения коллекции во время итерации
                foreach (var tree in FallenTrees.ToList())
                {
                    RespawnTree(tree);
                }
                pendingRespawn = false;
            }
        }

        private void RespawnTree(FallenTreeData data)
        {
            // Проверяем, что пенёк всё ещё на месте
            if (!WorldGen.InWorld(data.RootX, data.RootY)) return;
            var tile = Main.tile[data.RootX, data.RootY];
            if (!tile.HasTile) return;

            // Преобразуем FrameData в TrunkFrameData
            var frames = new List<TrunkFrameData>();
            for (int k = 0; k < data.Frames.Count; k++)
            {
                var f = data.Frames[k];
                frames.Add(new TrunkFrameData
                {
                    Y = k,
                    FrameX = f.FrameX,
                    FrameY = f.FrameY,
                    Color = new Color(f.R, f.G, f.B, f.A)
                });
            }

            // Создаем текстуру (теперь это безопасно, так как мы в PostUpdateWorld)
            var texture = TreeTextureBuilder.Build(frames, TileID.Trees, out int pivotX, out int pivotY);

            int projType = ModContent.ProjectileType<FallingTreeProjectile>();
            int proj = Projectile.NewProjectile(null, 0f, 0f, 0f, 0f, projType, 0, 0f, Main.myPlayer);

            if (Main.projectile[proj].ModProjectile is FallingTreeProjectile ft)
            {
                ft.InitFromSave(frames.Count, data.Direction, texture, pivotX, pivotY,
                    data.DropItemType, data.Angle, data.ChopProgress);
                Main.projectile[proj].Center = new Vector2(data.RootX * 16f + 8f, data.RootY * 16f + 16f);
                data.ProjectileId = proj; // Обновляем ID для будущих сохранений
            }
        }

        public static void RemoveTreeByProjectileId(int projId)
        {
            FallenTrees.RemoveAll(t => t.ProjectileId == projId);
        }

        public static void RegisterTree(int projId, int rootX, int rootY, float angle,
            int direction, int dropType, float chopProgress, int height, List<TrunkFrameData> frames)
        {
            var savedFrames = new List<FrameData>();
            foreach (var f in frames)
            {
                savedFrames.Add(new FrameData
                {
                    FrameX = f.FrameX,
                    FrameY = f.FrameY,
                    R = f.Color.R,
                    G = f.Color.G,
                    B = f.Color.B,
                    A = f.Color.A
                });
            }

            FallenTrees.Add(new FallenTreeData
            {
                ProjectileId = projId,
                RootX = rootX,
                RootY = rootY,
                Angle = angle,
                Direction = direction,
                DropItemType = dropType,
                ChopProgress = chopProgress,
                TreeHeightTiles = height,
                Frames = savedFrames
            });
        }
    }
}
