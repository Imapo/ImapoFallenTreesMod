using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace FallingTrees.Content.Tiles
{
    /// <summary>
    /// Один сегмент упавшего дерева. Ставится рядами при приземлении FallingTreeProjectile.
    /// Ведёт себя как обычный солид-тайл (блокирует движение игрока автоматически,
    /// это даёт нам физическую преграду "бесплатно"), рубится топором как обычное дерево.
    /// </summary>
    public class FallenLogTile : ModTile
    {
        public override void SetStaticDefaults()
        {
            Main.tileSolid[Type] = true;      // блокирует движение — препятствие
            Main.tileSolidTop[Type] = false;
            Main.tileFrameImportant[Type] = false;
            Main.tileAxe[Type] = true;        // рубится топором, не киркой
            Main.tileLighted[Type] = false;
            Main.tileBlockLight[Type] = true;

            TileID.Sets.CanBeClearedDuringGeneration[Type] = false;

            DustType = DustID.WoodFurniture;
            HitSound = SoundID.Dig;

            AddMapEntry(new Color(120, 85, 45));
        }

        public override void NumDust(int i, int j, bool fail, ref int num)
        {
            num = fail ? 1 : 3;
        }

        public override bool CanExplode(int i, int j) => true;

        public override void KillMultiTile(int i, int j, int frameX, int frameY)
        {
            // на случай если решите сделать сегмент многотайловым объектом (TileObjectData) —
            // сейчас FallenLogTile простой 1x1 тайл, поэтому этот метод не используется напрямую.
        }

        // В tModLoader нет метода "Drop" для ModTile — дроп предметов нужно делать
        // вручную внутри KillTile (и отключать стандартный, т.к. у 1x1 тайла без
        // ItemDrop-регистрации ничего своего и не выпадет).
        public override void KillTile(int i, int j, ref bool fail, ref bool effectOnly, ref bool noItem)
        {
            if (fail || effectOnly)
                return;

            Item.NewItem(new EntitySource_TileBreak(i, j), i * 16, j * 16, 16, 16, ItemID.Wood, 1);
            noItem = true; // мы уже сами заспавнили предмет, второй раз не нужно
        }
    }
}
