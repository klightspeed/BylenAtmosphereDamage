using System.Collections.Generic;
using Sandbox.Definitions;
using VRage.Collections;
using VRage.Game;
using VRage.Utils;

namespace AtmosphericDamage
{
    public static class Config
    {
        /////////////////////CHANGE THESE FOR EACH PLANET////////////////////////////

        public const string PLANET_NAME = "Bylen"; // this mod targets planet Bylen
        public const float LARGE_SHIP_DAMAGE = 1000f; // applies 1000 damage to each block per update at 30km
        public const float SMALL_SHIP_DAMAGE = 1000f;
        public const string DAMAGE_STRING = PLANET_NAME + "Atmosphere";
        public const float PLAYER_DAMAGE_AMOUNT = 100f;
        public const float OVERRIDE_ATMOSPHERE_HEIGHT = 200000f;
        public const float ATMOSPHERE_DAMAGE_EXPONENT = 30f; // Divides damage by 30 for every extra 30km

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
    }
}
