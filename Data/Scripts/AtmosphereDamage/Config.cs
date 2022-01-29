using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Collections;
using VRage.Game;
using VRage.Utils;

namespace BylenAtmosphericDamage
{
    public static class Config
    {
        /////////////////////CHANGE THESE FOR EACH PLANET////////////////////////////

        public const string PLANET_NAME = "Bylen"; // this mod targets planet Bylen
        public static float LARGE_SHIP_RAD_DAMAGE = 1000f; // applies 1000 damage (scaled by area) to each block per update at the top of the atmosphere
        public static float SMALL_SHIP_RAD_DAMAGE = 1000f;
        public static float PLAYER_RAD_DAMAGE = 5000f;
        public static float LARGE_SHIP_MAX_DAMAGE = 1000f;
        public static float SMALL_SHIP_MAX_DAMAGE = 1000f;
        public static float PLAYER_MAX_DAMAGE = 20f;
        public const string DAMAGE_STRING = PLANET_NAME + "Atmosphere";
        public static float RADIATION_FALLOFF_DIST = 3000f; // inverse square falloff distance

        public const double EMITTER_DRAW_DIST = 10000;
        /////////////////////////////////////////////////////////////////////////////

        ///////////////////NEVER EVER CHANGE THESE///////////////////////////////////

        public const int UPDATE_RATE = 20; //damage will apply every 200 frames. MUST ALWAYS BE DIVISIBLE BY 10!
        public const int MAX_QUEUE = UPDATE_RATE * 8; //damage 4 objects per tick

        //message handler IDs
        //I literally just mashed my keyboard to get these
        public const long INIT_ID = 1684445163654733187;
        public const long INIT_INHIBIT_ID = 1684445163654733188;
        public const long DAMAGE_LIST_ID = 1684445163654733189;
        public const long DRAW_LIST_ID = 1684445163654733190;
        public const long PARTICLE_LIST_ID = 1684445163654733191;
        public const ushort NETWORK_ID = 51287;

        private const string CONFIG_FILE_NAME = "config.txt";

        public static void ReadConfig()
        {
            if (MyAPIGateway.Utilities.FileExistsInWorldStorage(CONFIG_FILE_NAME, typeof(Config)))
            {
                using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(CONFIG_FILE_NAME, typeof(Config)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        var name = parts[0].Trim().ToLower();
                        var value = parts[1].Trim();
                        float val;

                        if (float.TryParse(value, out val))
                        {
                            switch (name)
                            {
                                case "large_ship_rad_damage": LARGE_SHIP_RAD_DAMAGE = val; break;
                                case "large_ship_max_damage": LARGE_SHIP_MAX_DAMAGE = val; break;
                                case "small_ship_rad_damage": SMALL_SHIP_RAD_DAMAGE = val; break;
                                case "small_ship_max_damage": SMALL_SHIP_MAX_DAMAGE = val; break;
                                case "player_rad_damage": PLAYER_RAD_DAMAGE = val; break;
                                case "player_max_damage": PLAYER_MAX_DAMAGE = val; break;
                                case "radiation_falloff_dist": RADIATION_FALLOFF_DIST = val; break;
                            }
                        }
                    }
                }
            }
        }
    }
}
