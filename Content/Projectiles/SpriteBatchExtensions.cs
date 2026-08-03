using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria.GameContent;

namespace ImapoFallingTrees.Content.Projectiles
{
    public static class SpriteBatchExtensions
    {
        public static void DrawLine(this SpriteBatch sb, Vector2 start, Vector2 end, Color color, int thickness)
        {
            float distance = Vector2.Distance(start, end);
            float angle = (float)Math.Atan2(end.Y - start.Y, end.X - start.X);
            
            for (int i = 0; i < distance; i += 2)
            {
                float x = start.X + (float)Math.Cos(angle) * i;
                float y = start.Y + (float)Math.Sin(angle) * i;
                sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle((int)x, (int)y, thickness, thickness), color);
            }
        }
    }
}
