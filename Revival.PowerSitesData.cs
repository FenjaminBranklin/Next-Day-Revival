// Power 2: declared power sites. Mirrors research/airfield_power_layout.json
// (site id, generator id/position/yaw, day tank minutes, consumers and their
// guns) and the bunker recipe's sockets (unity/EastTile/Content/
// east_af_generator.json); research/power_network_check.py keeps them equal.
// A new site (the Primorye base later) is one more entry here plus its layout
// declaration; nothing in the runtime looks up a fixed airfield.
namespace NextDayRevival
{
    internal static class PowerSitesData
    {
        internal static readonly PowerSite[] Sites = Build();

        static PowerSite[] Build()
        {
            PowerSite main = new PowerSite();
            main.Id = "main_airfield"; main.Generator = "AF-GEN";
            main.X = 4070f; main.Z = 1220f; main.Yaw = 0f;
            // pb-5 concrete bunker (assets/src/power_bunker_blender.py): 11 x 7 m
            // outside, 1 m walls and roof slab, floor to roof top 4.2 m
            // (30.8 x 19.6 u, 11.76 u). Bunker-local frame: the root sits on the
            // interior floor (east_af_generator.json sockets).
            main.HalfX = 15.4f; main.HalfZ = 9.8f; main.Height = 11.76f; main.WallM = 1.0f;
            main.BreakerX = 10.64f; main.BreakerY = 3.36f; main.BreakerZ = -2.52f;
            main.TankX = -10.36f; main.TankY = 3.36f; main.TankZ = 4.2f;
            // Outside the south blast door, on the wall west of the opening
            // (clear of the leaf's outward swing), facing south.
            main.SignX = 0.56f; main.SignY = 4.2f; main.SignZ = -9.87f; main.SignYaw = 90f;
            main.Armour = PowerNetCore.ArmourPoints;
            main.TankMinutes = 45f;
            main.Consumers = new int[] { PowerNetCore.Radar, PowerNetCore.AirDefence, PowerNetCore.Lights,
                PowerNetCore.Terminal, PowerNetCore.Screens };
            // AA-NW, AA-NE, AA-S, ZU-W (FlakPositionsCore.GunIndex).
            main.Guns = new int[] { 0, 6, 1, 5 };
            return new PowerSite[] { main };
        }
    }
}
