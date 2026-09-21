using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    // ---------------------------------------------------- NPC-versus-NPC combat
    //
    // WHAT THIS ADDS
    //
    //   The vanilla AI only ever fights players: sensing reads
    //   NetworkGameServer.localPlayer, target allocation resolves Photon PLAYER
    //   ids, and NPC_FirearmWeaponController.FireOneShot has no branch that
    //   damages NPC infantry. Two NPCs next to each other never fight.
    //
    //   NpcWar runs OPERATIONS. An operation is one squad a heli troop landing
    //   (RevivalTroopInsertion) has put on the ground, plus the combat arrow the
    //   admin drew in the editor:
    //
    //     ToStart   only when the landing zone is well away from the arrow
    //               tail: the squad runs there first
    //     Advance   the squad ASSAULTS along the arrow toward the head and
    //               attacks everything it sees that its faction hates
    //     Patrol    the survivors sweep the arrow up and down until the patrol
    //               time (at most two hours) is over
    //     removed   the living men of the squad leave the map; the dead stay
    //               CorpseMinutes for their loot (6.17)
    //
    //   THE ARROW IS A PATH (6.18). It has a tail, a head and up to six bends
    //   in between, and the squad walks the legs in order. A bend is how the
    //   admin routes them around a lake or a wall; the HEAD is the objective
    //   and nothing else is. Two things follow from that and both are new:
    //   a contact beside the arrow may turn the line but may not walk it
    //   further off the drawn leg than ArrowCorridor (Anchor -> Corridor), and
    //   a man who stops covering ground is put back on his feet (Unstick) -
    //   because far from every player the terrain colliders are off, a walk
    //   point then keeps the arrow's own height, the NavMesh sample beside it
    //   misses, and the whole squad waits for a player to come and look at it.
    //
    // HOW THE SQUAD FIGHTS (6.16.5, after the 6.16.4 field report)
    //
    //   The user's order: side by side, briskly in the combat direction, fire
    //   at once, shoot exactly like every other enemy in the game, and never
    //   more than a few steps in a direction that is not the fight. So:
    //
    //   ONE LINE. Every man has a lane - a fixed lateral place in a line
    //   abreast across the direction of advance (LineSpacing apart, a second
    //   rank only above ten men). The whole line runs at a point Lead units
    //   ahead of its own centre along the arrow, so nobody arrives and stops
    //   while there is ground to take; a man who has got ahead of the line
    //   walks until it catches up. There is no cover search, no sideways
    //   firing position and no flanking any more: those were the "aimless
    //   wandering" of the field report.
    //
    //   CONTACT. A man who sees a hostile NPC - or the player his own AI has
    //   as kill target - inside AssaultRange stops where he is and fires
    //   within a fraction of a second. The line turns toward the enemy (up to
    //   100 degrees off the arrow) and keeps closing until CloseRange.
    //   Between, the two halves of the squad alternate every BoundSeconds:
    //   one half keeps firing, the other runs one bound FORWARD and fires
    //   again from there. A man with no line of fire runs on with the line
    //   until he has one.
    //
    //   THE SHOT IS THE GAME'S. NPC_AI2.ShootingActions fires only in
    //   MainState Idle + AdditionalState Shooting, every
    //   _shootingTimerDelayCached (0.2-0.3 s), without bursts, and
    //   LookAtIkController points the weapon while the man is in Aiming or
    //   Shooting (CONFIRMED IL, REVERSE_ENGINEERING "An NPC only aims its
    //   weapon at a PLAYER"). A firing man is held in exactly that state:
    //     - at a PLAYER the vanilla code does everything itself - the shot,
    //       its hit chance and the aim - and NpcWar only keeps him standing;
    //     - at an NPC the vanilla code has no target, so NpcWar drives the
    //       same aim IK and calls the NPC's own
    //       NPC_FirearmWeaponController.FireTo at the same cadence, which
    //       brings rate of fire, magazine, muzzle flash and sound on every
    //       client (RPC NetworkWeaponState). FireOneShot has no NPC damage
    //       branch, so the hit is our raycast through NPC_AI2.ApplyDamage.
    //   An empty magazine is the native reload (OnBulletsEnded). No ready
    //   native weapon means no shot, no effect and no damage.
    //
    //   FEET FIRST (6.16.6, after the 6.16.5 field report). The shooting clips
    //   are upper-body layers, so Shooting straight out of a run left the legs
    //   running on the spot. A man is first put into Idle + Aiming, which
    //   replaces the whole-body clip, and fires only once he is planted
    //   (Planted). A target that ducks away for a moment keeps him standing
    //   with the weapon up (Steady) instead of sprinting off for every lost
    //   glimpse. Walking while firing does not exist for NPCs: the game forces
    //   AdditionalState 0 in any MainState but Idle.
    //
    //   ONLY NPCS THAT CAN BE HURT. A target must be one NPC_AI2.ApplyDamage
    //   really damages - not a StoreKeeper, not in a safe settlement, not
    //   uninitialized - and enlisted defenders are checked like everyone else
    //   (Targetable, Hurtable). The 6.16.5 squad hung on a settlement trader.
    //
    //   Defenders - NPCs the squad attacks, and comrades close to them - fire
    //   back the same way and may still take cover; they are the enemy, not
    //   the assault.
    //
    // CLASSES (6.17, after the 6.16.7 field report). Every soldier of a heli
    //   landing carries an editor class (SquadClass; not to be confused with
    //   the "defenders" above, who are the enemy):
    //     regular   the assault line above
    //     sniper    keeps SniperBack behind the line, engages out to SniperRange
    //               with SniperAccuracy and one round every SniperShotSeconds,
    //               never bounds. 6.19: he is the man the squad picked for the
    //               job. Cover, movement and being shot at cost him SniperSteady
    //               of what they cost a rifleman, his own skill draw counts for
    //               SniperSkill, his hit chance falls by SniperFalloff over the
    //               whole of SniperRange, and his TAC-50 round takes
    //               SniperDamage times DamagePerShot. He is slow and he hits.
    //     tank      runs TankLead ahead of the line, closes to TankCloseRange,
    //               bounds on his own rhythm, never waits out a lost glimpse
    //     defender  (Heavy) DefenderHealth times the hit points and, left empty
    //               in the editor, the full UKB set with the exoskeleton. Below
    //               DefenderRegenBelow of his health he kneels in the game's own
    //               boss regeneration (NPC_AI2.StartRegeneration: MainState 12,
    //               a 12 s pause, 25 percent of HealthMax back over 10 s, clips
    //               ukb_boss_regen_* which the Marauder model carries). He cannot
    //               fire then and takes DefenderRegenDamage times the damage:
    //               the players' window.
    //     antitank  keeps AntiTankBack behind the line, fires his rifle only
    //               inside AntiTankSelfDefense and flies the FPV drone
    //               (CrewDrone.LaunchAt) at the enemy the squad is fighting.
    //               Against a vehicle he sends the drone first, then draws the
    //               M72 LAW (AntiTankRockets rounds in all), runs into
    //               AntiTankLawRange and fires the player LAW's blast.
    //   ARMOUR scales every hit on a squad man (prefix on NPC_AI2.ApplyDamage):
    //   the worn items' Regenerate value, weighted by body coverage and doubled
    //   for UKB parts over the exoskeleton - the player's own gear rule - up to
    //   ArmorMaxReduction.
    //
    // BALANCE (6.19, after the field report on 6.18.0). The squad behaves the
    //   way it should now, but "no party can be put together that really wipes
    //   the settlement" - the 6.16.7 correction of E-057 went too far. Three
    //   numbers move, and only three:
    //     - a landing may set down 24 men instead of 16
    //       (RevivalTroopInsertion.MaxSquad); over twenty they form three ranks
    //       so the front stays as wide as sixteen men's was (LayOut).
    //     - SquadFalloff 0.55 -> 0.4. The squad loses the same share of its hit
    //       chance over its range as a defender does over his; 6.16.7 had
    //       punished it twice, once with the allowance removed and once with the
    //       steeper curve.
    //     - DefenderHitsToKill 2 -> 3, so an unarmoured squad man survives one
    //       more round of return fire (DefenderRound).
    //   Everything else about the defenders - their reach, their target pick,
    //   the 120 unit enlist radius, MaxDefenders - stands as 6.16.7 left it.
    //   HYPOTHESIS until the field run: this is the middle ground between
    //   E-057's "wiped a settlement for 3 losses" and 6.18.0's stalemate.
    //
    // VEHICLES (6.17). A crewed patrol vehicle of a hated faction, or any
    //   vehicle with a hostile player aboard, within 1.5 x AssaultRange is the
    //   squad's threat when no infantry is: the line stops at VehicleStandoff
    //   and its rifles fire at the hull (rifles do no vehicle damage); the
    //   anti-tank gunner does the killing through the game's ExplosionObject.
    //
    // SMOOTH RUNNING (6.17). NPC_AI2.NavAgentMoveToPos clears the path with
    //   isStopped = true before it sets the new one (CONFIRMED IL), so every
    //   full move order cost a running man his speed - HYPOTHESIS: the hitch
    //   before each step in the 6.16.7 report. A running man whose point moves
    //   is re-aimed on his NavMeshAgent directly (Retarget); the full order, the
    //   RPC the other clients follow, goes out at most every FullOrderSeconds
    //   and keeps the speed the path reset would have cost.
    //
    // CORPSES (6.17). The 6.16.7 log: "ended (wiped out) - 15 men removed from
    //   the map", bodies and loot gone. An operation that ends removes only its
    //   living men; the dead and their settlement stay CorpseMinutes, longer
    //   while a player stands by.
    //
    // 6.16.0 FIELD BUG. InitSpawnNpc parents every NPC under the settlement's
    //   AllPeopleTr, so the settlement object is never moved; only the
    //   walk-point root follows the squad and the men move by NavMesh orders.
    //
    // SCALE. The game world is modelled about 2.8 times real size: a human NPC
    //   capsule is 5.0 units tall, so every distance below is in those units.
    //
    // AUTHORITY
    //
    //   Only the Photon master client decides anything, and only on NPCs whose
    //   photonView.isMine is true - the two conditions ApplyDamage checks.
    //   Nothing here runs without an operation, so a map without troop
    //   landings is unchanged. The aim IK is visual and driven where the fight
    //   is computed; a remote client sees the synchronised state, body yaw and
    //   the weapon's own muzzle flash.
    //
    public static class NpcWar
    {
        // -------------------------------------------------------------- config

        internal static ConfigEntry<float> CfgSightRange;
        internal static ConfigEntry<float> CfgDamage;
        internal static ConfigEntry<float> CfgAccuracy;
        internal static ConfigEntry<float> CfgSpread;
        internal static ConfigEntry<int>   CfgMaxCombatants;
        internal static ConfigEntry<bool>  CfgDebug;
        internal static ConfigEntry<float> CfgCoverChance;
        internal static ConfigEntry<bool>  CfgSuppression;
        // New in 6.16.5. New keys on purpose: Config.Bind keeps the value an
        // installed nextday.revival.toolkit.cfg already has, so a changed
        // DEFAULT would never reach a player (CLAUDE.md, point 4). The keys
        // of the old cover/flank squad (EngageRange, FireInterval,
        // ReactionMax, BurstMax, MaxDetour, Spacing, Standoff, FlankSeconds)
        // are no longer bound.
        internal static ConfigEntry<float> CfgLineSpacing;
        internal static ConfigEntry<float> CfgAssaultRange;
        internal static ConfigEntry<float> CfgCloseRange;
        internal static ConfigEntry<float> CfgBoundSeconds;
        // New in 6.17: corpses, vehicles, armour and the soldier classes.
        internal static ConfigEntry<float> CfgCorpseMinutes;
        internal static ConfigEntry<float> CfgVehicleStandoff;
        internal static ConfigEntry<bool>  CfgFpvDrones;
        internal static ConfigEntry<float> CfgArmorMaxReduction;
        internal static ConfigEntry<float> CfgSniperRange;
        internal static ConfigEntry<float> CfgSniperAccuracy;
        internal static ConfigEntry<float> CfgSniperShotSeconds;
        internal static ConfigEntry<float> CfgSniperBack;
        internal static ConfigEntry<float> CfgSniperDamage;
        internal static ConfigEntry<float> CfgTankLead;
        internal static ConfigEntry<float> CfgTankCloseRange;
        internal static ConfigEntry<float> CfgDefenderHealth;
        internal static ConfigEntry<float> CfgDefenderRegenBelow;
        internal static ConfigEntry<int>   CfgDefenderRegenCount;
        internal static ConfigEntry<float> CfgDefenderRegenDamage;
        internal static ConfigEntry<float> CfgAntiTankBack;
        internal static ConfigEntry<int>   CfgAntiTankRockets;
        internal static ConfigEntry<float> CfgAntiTankLawRange;
        internal static ConfigEntry<float> CfgAntiTankSelfDefense;
        internal static ConfigEntry<int>   CfgAntiTankDrones;
        internal static ConfigEntry<float> CfgAntiTankDroneSeconds;
        // New in 6.18: the arrow may bend, it is a corridor and not a
        // suggestion, and a squad that stops walking is put back on its feet.
        internal static ConfigEntry<float> CfgArrowCorridor;
        internal static ConfigEntry<float> CfgStuckSeconds;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgSightRange = cfg.Bind("NpcWar", "SightRange", 110f,
                "Groesste Entfernung (Meter), auf die ein angegriffener NPC einen "
                + "Truppsoldaten als Ziel annimmt und zurueckschiesst. Mindestens "
                + "AssaultRange; nur ein hoeherer Wert erweitert sie.");
            CfgDamage = cfg.Bind("NpcWar", "DamagePerShot", 18f,
                "Schaden je Treffer an einem NPC. Das Spiel wertet den Treffer als "
                + "Kopftreffer und verdreifacht ihn: 18 nimmt 54 Lebenspunkte. Fuer "
                + "den Verteidiger gilt dieser Wert nicht: sein Schuss wird so "
                + "berechnet, dass genau drei Treffer einen unbeschaedigten "
                + "Truppsoldaten toeten (DefenderHitsToKill, seit 6.19 drei statt "
                + "zwei). Ruestung rechnet danach herunter.");
            CfgAccuracy = cfg.Bind("NpcWar", "Accuracy", 0.6f,
                "Trefferwahrscheinlichkeit auf kurze Entfernung (0..1); sie faellt "
                + "zur Reichweite hin bei beiden Seiten um 40 Prozent ab. Deckung, "
                + "Hinknien und das persoenliche Koennen des Schuetzen veraendern "
                + "sie zusaetzlich.");
            CfgSpread = cfg.Bind("NpcWar", "MissSpread", 1.6f,
                "Wie weit (Meter) ein verfehlter Schuss neben dem Ziel einschlaegt.");
            CfgMaxCombatants = cfg.Bind("NpcWar", "MaxDefenders", 32,
                "Sicherheitsgrenze: so viele angegriffene NPCs duerfen gleichzeitig "
                + "zurueckschiessen (alle Einsaetze zusammen).");
            CfgDebug = cfg.Bind("NpcWar", "Debug", false,
                "Ausfuehrliche Log-Zeilen und eine Statuszeile oben links.");
            CfgCoverChance = cfg.Bind("NpcWar", "CoverChance", 0.6f,
                "Wie bereitwillig ein ANGEGRIFFENER NPC im Feuerkampf Deckung sucht "
                + "(0 = nie, 1 = bei jeder Gelegenheit). Der Landetrupp selbst sucht "
                + "keine Deckung, er greift an.");
            CfgSuppression = cfg.Bind("NpcWar", "Suppression", true,
                "Nahe Einschlaege machen einen angegriffenen NPC unsicherer: er "
                + "schiesst schlechter und sucht eher Deckung.");

            CfgLineSpacing = cfg.Bind("NpcWar", "LineSpacing", 8f,
                "Abstand (Meter) zwischen zwei Nachbarn in der Angriffslinie des "
                + "Landetrupps. Bis zehn Mann eine Linie, darueber zwei Reihen.");
            CfgAssaultRange = cfg.Bind("NpcWar", "AssaultRange", 180f,
                "Groesste Entfernung (Meter), auf die der Landetrupp einen sichtbaren "
                + "Gegner (NPC oder Spieler) sofort beschiesst.");
            CfgCloseRange = cfg.Bind("NpcWar", "CloseRange", 30f,
                "Naeher als diese Entfernung (Meter) rueckt der Trupp nicht auf einen "
                + "Gegner vor, er bleibt stehen und schiesst.");
            CfgBoundSeconds = cfg.Bind("NpcWar", "BoundSeconds", 4f,
                "Im Feuerkampf wechseln die zwei Haelften des Trupps nach so vielen "
                + "Sekunden: eine schiesst, die andere springt ein Stueck vor.");
            CfgArrowCorridor = cfg.Bind("NpcWar", "ArrowCorridor", 60f,
                "So weit (Meter) darf ein Gegner die Linie seitlich vom gezeichneten "
                + "Pfeil wegziehen. Das Ziel bleibt immer die Pfeilspitze; ohne diese "
                + "Grenze lief der Trupp jedem Kontakt neben dem Pfeil hinterher. "
                + "0 = die Linie bleibt genau auf dem Pfeil.");
            CfgStuckSeconds = cfg.Bind("NpcWar", "StuckSeconds", 6f,
                "Steht ein Soldat so lange still, obwohl er laufen soll, bekommt er "
                + "einen neuen Befehl und notfalls seinen NavMesh-Platz zurueck. Weit "
                + "weg von jedem Spieler sind die Gelaende-Collider aus, und ohne das "
                + "bleibt der Trupp liegen, wo ihn niemand sieht. 0 = aus.");

            CfgCorpseMinutes = cfg.Bind("NpcWar", "CorpseMinutes", 20f,
                "So viele Minuten bleiben die Toten eines beendeten Einsatzes liegen, "
                + "damit man sie pluendern kann (1..120). Steht ein Spieler daneben, "
                + "bleiben sie laenger. Nur die Lebenden verlassen die Karte sofort.");
            CfgVehicleStandoff = cfg.Bind("NpcWar", "VehicleStandoff", 70f,
                "Gegen ein feindliches Fahrzeug bleibt die Linie in diesem Abstand "
                + "(Meter) stehen; nur der Panzerabwehrschuetze rueckt vor.");
            CfgFpvDrones = cfg.Bind("NpcWar", "FpvDrones", true,
                "Panzerabwehrschuetzen des Landetrupps fliegen FPV-Drohnen gegen "
                + "NPCs und Fahrzeuge.");
            CfgArmorMaxReduction = cfg.Bind("NpcWar", "ArmorMaxReduction", 0.7f,
                "Hoechstens so viel Schaden (0..0,9) nimmt die Ruestung eines "
                + "Truppsoldaten weg. Gerechnet wird wie beim Spieler: Regenerate-Wert "
                + "der getragenen Teile, UKB-Teile mit Exoskelett doppelt.");

            CfgSniperRange = cfg.Bind("NpcWarClasses", "SniperRange", 380f,
                "Scharfschuetze: groesste Kampfentfernung (Meter).");
            CfgSniperAccuracy = cfg.Bind("NpcWarClasses", "SniperAccuracy", 0.97f,
                "Scharfschuetze: Trefferwahrscheinlichkeit auf kurze Entfernung (0..1); "
                + "sie faellt zur Reichweite hin nur um 8 Prozent ab. Deckung, "
                + "Bewegung und Beschuss kosten ihn nur 40 Prozent dessen, was sie "
                + "einen Schuetzen der Linie kosten, und sein persoenliches Koennen "
                + "zaehlt nur zu einem Drittel: er ist der ausgesuchte Schuetze. "
                + "Bezahlt wird das mit SniperShotSeconds.");
            CfgSniperShotSeconds = cfg.Bind("NpcWarClasses", "SniperShotSeconds", 4f,
                "Scharfschuetze: Sekunden zwischen zwei Schuessen. Er trifft fast "
                + "immer, aber langsam.");
            CfgSniperBack = cfg.Bind("NpcWarClasses", "SniperBack", 45f,
                "Scharfschuetze: so viele Meter bleibt er hinter der Linie.");
            CfgSniperDamage = cfg.Bind("NpcWarClasses", "SniperDamage", 2f,
                "Scharfschuetze: Faktor auf DamagePerShot (0,5..6). Die TAC-50 ist "
                + "ein Anti-Material-Gewehr; mit 2 nimmt ein Treffer 108 statt 54 "
                + "Lebenspunkte. 1 = dieselbe Wirkung wie ein Gewehr der Linie.");
            CfgTankLead = cfg.Bind("NpcWarClasses", "TankLead", 18f,
                "Sturmsoldat (tank): so viele Meter laeuft er vor der Linie.");
            CfgTankCloseRange = cfg.Bind("NpcWarClasses", "TankCloseRange", 10f,
                "Sturmsoldat (tank): so nah (Meter) rueckt er an den Gegner heran.");
            CfgDefenderHealth = cfg.Bind("NpcWarClasses", "DefenderHealth", 1.5f,
                "Defender: Faktor auf die Lebenspunkte (Patrol/CrewHealth).");
            CfgDefenderRegenBelow = cfg.Bind("NpcWarClasses", "DefenderRegenBelow", 0.5f,
                "Defender: unter diesem Anteil seiner Lebenspunkte kniet er nieder und "
                + "regeneriert (0,1..0,9). Solange schiesst er nicht.");
            CfgDefenderRegenCount = cfg.Bind("NpcWarClasses", "DefenderRegenCount", 2,
                "Defender: so oft darf er je Einsatz regenerieren.");
            CfgDefenderRegenDamage = cfg.Bind("NpcWarClasses", "DefenderRegenDamage", 1.3f,
                "Defender: Schadensfaktor, solange er kniet (das Fenster fuer die Spieler).");
            CfgAntiTankBack = cfg.Bind("NpcWarClasses", "AntiTankBack", 25f,
                "Panzerabwehrschuetze: so viele Meter bleibt er hinter der Linie.");
            CfgAntiTankRockets = cfg.Bind("NpcWarClasses", "AntiTankRockets", 4,
                "Panzerabwehrschuetze: LAW-Raketen je Einsatz, nur gegen Fahrzeuge.");
            CfgAntiTankLawRange = cfg.Bind("NpcWarClasses", "AntiTankLawRange", 90f,
                "Panzerabwehrschuetze: aus dieser Entfernung (Meter) feuert er die LAW.");
            CfgAntiTankSelfDefense = cfg.Bind("NpcWarClasses", "AntiTankSelfDefense", 35f,
                "Panzerabwehrschuetze: sein Gewehr benutzt er nur gegen Gegner, die "
                + "naeher sind (Meter).");
            CfgAntiTankDrones = cfg.Bind("NpcWarClasses", "AntiTankDrones", 3,
                "Panzerabwehrschuetze: FPV-Drohnen je Einsatz.");
            CfgAntiTankDroneSeconds = cfg.Bind("NpcWarClasses", "AntiTankDroneSeconds", 45f,
                "Panzerabwehrschuetze: Sekunden zwischen zwei Drohnen.");
        }

        internal static bool DronesEnabled
        {
            get { return CfgFpvDrones == null || CfgFpvDrones.Value; }
        }

        // -------------------------------------------------------- world scale

        const float ChestHeight = 3.3f;     // of a 5.0 unit NPC capsule
        const float HeadHeight = 4.6f;
        const float WaistHeight = 2.2f;
        const float EyeHeight = 4.2f;
        const float CrouchEye = 2.7f;
        const float Arrive = 25f;           // the line counts as arrived
        const float StartDistance = 60f;    // landing zone this far from the tail: run there first
        const float Lead = 30f;             // the line runs at a point this far ahead of itself
        const float RankDepth = 12f;        // each rank this far behind the one in front of it
        const int PerRank = 10;             // up to this many men before another rank is opened
        const float BoundStep = 20f;        // one forward bound under fire
        const float LaneSlack = 4f;         // this close to his point a man is there
        const float AheadWalk = 12f;        // this far ahead of the line he walks ...
        const float AheadRun = 4f;          // ... until the line is back within this
        const float Catchup = 30f;          // this far behind his place: close up before firing
        const float PlantSeconds = 0.35f;   // standing in the aim clip before Shooting
        const float SteadySeconds = 1.2f;   // a target out of sight this long: still stand
        const float SquadFalloff = 0.4f;    // squad hit chance lost at full AssaultRange
        const float SniperFalloff = 0.08f;  // a marksman keeps almost all of it out to SniperRange
        const float SniperSteady = 0.4f;    // what suppression, cover and movement cost him of a rifleman's penalty
        const float SniperSkill = 0.35f;    // how much of his personal skill draw still counts
        const float TryDamageHead = 3f;     // Turret.TryDamage hits count as Head: x3 in NPC_AI2
        const int DefenderHitsToKill = 3;   // defender rounds that kill a full-health squad man
        const float EnlistRadius = 120f;    // same-faction NPCs this close to a struck one join
        const float FullOrderSeconds = 3.5f; // a running man gets a full (RPC) move order at most this often
        const float RetargetSlack = 4f;     // a point that moved less than this is left alone
        const float RetargetAngle = 40f;    // a point that turned further gets a full order
        const float PlayerNearCorpse = 60f; // a player this close keeps the dead on the map
        const float VehicleAim = 3f;        // aim height on a vehicle hull
        const float FarTick = 250f;         // no player this close and no contact: a third of the men per frame
        const float StuckMove = 1.5f;       // less ground than this covered counts as standing still
        const float StallSeconds = 30f;     // no progress on the leg this long: one line of evidence
        const float RescueQuiet = 150f;     // a man is only put back on the NavMesh with no player this close
        const float NpcDroneAim = 2.5f;     // FPV aim height on an NPC (chest of the 5 unit capsule)
        const float SquadLawDamage = 900f;  // the player LAW's blast (RocketHook, VehicleArmor)
        const float SquadLawRadius = 12f;
        const int LawId = 1162;             // Crew.LAW_ID, the M72 LAW
        const int ExoskeletonId = 6019;     // UKB exoskeleton, backpack slot
        const int UkbHelmetId = 4017, UkbBodyId = 4316, UkbLegsId = 4509, UkbHandsId = 4603;
        const int SniperRifleId = 1161;     // TAC-50, a sniper's default
        const int MachineGunId = 1160;      // MG42, the tank's and the defender's default
        const int RifleFallbackId = 1001;   // the anti-tank gunner's rifle when none is chosen

        // NPC_AI2 states. NPCMainState: Idle 0, Walk 1, Run 2.
        // NPCAdditionalState: Empty 0, Aiming 1, Reloading 2, Shooting 3.
        // NPCPoseState: Normal 0, Crouch 1, Crawl 2. SwitchAnimationByStates
        // forces AdditionalState 0 whenever MainState is not Idle, and the
        // Marauder prefab has no walk or crouch aiming clip: a man aims and
        // fires standing still, exactly like every vanilla NPC.
        const int MainIdle = 0, MainWalk = 1, MainRun = 2;
        const int MainRegen = 12;            // NPCMainState.Regeneration
        const int AddNone = 0, AddAim = 1, AddFire = 3;
        const int AddReload = 2;
        const int PoseStand = 0, PoseCrouch = 1;
        // NPCBehaviorPattern: Aggressive 0, StoreKeeper 1, Harmless 2, Boss 3.
        // NPC_AI2.ApplyDamage skips DecreaseHealth for a StoreKeeper.
        const int BehaviorStoreKeeper = 1;

        // ------------------------------------------------------- runtime state

        enum Phase { ToStart, Advance, Patrol }

        /// <summary>What a man is doing this second. For the debug line and
        /// for deciding what to order once.</summary>
        enum Stance { Advance, Fire, Bound, Hold, Reposition }

        /// <summary>The editor's soldier classes (troopdef.SQUAD_CLASSES):
        /// regular, sniper, "tank" (Assault), "defender" (Heavy), antitank.</summary>
        internal enum SquadClass { Regular, Sniper, Assault, Heavy, AntiTank }

        /// <summary>One NPC in a fight: a squad man or a defender.</summary>
        class Fighter
        {
            public Component Ai;          // NPC_AI2
            public Transform Tr;
            public Squad Squad;           // null = defender
            public object Faction;        // Fraction enum value
            public Array Hated;           // Fraction[] this NPC hates
            public Transform Target;
            public bool TargetIsPlayer;   // Target is his vanilla _killTarget
            public float NextScan, NextShot, ReactUntil, NextLos, LastSeen;
            public bool Sees;
            public float AimHeight = ChestHeight;  // the part of the target he can see
            public float Skill = 1f;      // marksmanship multiplier, drawn once

            // His place in the assault line.
            public float LaneOffset, RankOffset;
            public int Team;              // 0/1: the two halves that bound in turn
            public float FireSince, BoundUntil, NextBound, BlindUntil;
            public Vector3 BoundDest;
            // Since when his feet stand in a standing aim or shooting state
            // (0 = not planted), and until when a target that has just gone
            // out of sight still keeps him standing.
            public float PlantedSince, SteadyUntil;
            public float NextTargetCheck;   // defender: next Targetable re-check
            public int StepFailures;        // squad man: consecutive ManStep exceptions

            // 6.17: his class, what his armour leaves of a hit, the kneeling
            // regeneration, the anti-tank gunner's drone, LAW and weapon switch.
            public SquadClass Class;
            public float ArmorScale = 1f;
            public int RegenUsed;
            public float RegenCheck, RegenHoldUntil;
            public int RifleId, LawLeft, SwitchTo, SwitchPhase;
            public float SwitchSince, NextSwitchWarn;
            public int DroneId, DronesUsed;
            public float NextDrone, DroneHoldUntil;
            public Transform DroneTarget;
            public float LastFullOrder;     // Time.time of the last full (RPC) move order
            public float GroundPause;

            // 6.18: where he stood when he last covered ground, and when. A man
            // told to run who does not move is put back on his feet (Unstick).
            public Vector3 LastPos;
            public float MovedAt, NextUnstuck;
            public int Unstuck;

            // Defender posture.
            public float Nerve = 1f, Pace = 1f;
            public float Suppression, Hurt, NextHurt, NextCover;
            public bool InCover, Crouched;
            public Vector3 Cover;

            // Orders. What we last told the game, so a state is only re-sent
            // when it really changes - every SetStateWithAnimAndSync is an RPC
            // and restarts the animation.
            public Stance Stance = Stance.Advance;
            public bool HasOrder;
            public Vector3 Ordered;
            public GameObject Point;      // the walk point currently issued
            public float NextMove, MoveDeadline;
            public int WantMain = -1, WantAdd = -1, WantPose = -1;
            public float NextState, PauseUntil;

            // Weapon and aim IK, resolved per man on first use.
            public Component Ik;
            public Transform Look;
            public bool IkMissing, IkDriven;
            public float NextIkRetry, AimWeight, PoseSince;
            public Component Wm;
            public bool Armed, EquipWarned;
            public float NextEquip, SlotStuckSince;
            public int EquipTries;
            public int WeaponId;
            public float MuzzleBlockedSince, MateBlockedSince;
        }

        class Squad
        {
            public bool GroundGroup, GroundWalking;
            public float GroundRadius;
            public string Tag;
            public GameObject Settlement;
            public Transform WalkRoot;       // AllWalkPointsTr: follows the body
            public readonly List<Fighter> Men = new List<Fighter>();
            public Vector3 Lz;
            // The combat arrow the editor drew. Two points for a straight one,
            // more when it was bent around an obstacle; LegIndex is the segment
            // the squad is walking, Path[LegIndex] -> Path[LegIndex + 1]. The
            // head is the objective, always: every leg before it is only the
            // way the admin wants them to take there.
            public readonly List<Vector3> Path = new List<Vector3>();
            public int LegIndex;
            public Vector3 Tail { get { return Path[0]; } }
            public Vector3 Head { get { return Path[Path.Count - 1]; } }
            public Phase Phase;
            public bool TowardHead = true;   // patrol leg
            public float PatrolSeconds, PatrolEnds, HardEnd, NextRing;
            public Vector3 Centre, Line, Front;
            // The advance watchdog: how far the line has come on this leg at
            // best, when that last improved, and how long the leg is.
            public float Along, AlongSince, LegLen, NextStall;
            // With no contact and no player near, a third of the men is stepped
            // per frame - nobody can see the other two thirds.
            public bool Quiet;
            public float NextQuiet;
            public int StepOffset;

            // The contact picture, refreshed four times a second.
            public Transform Threat;
            public bool ThreatSeen;          // some man has a line of fire to it
            public float NextThreat, ThreatUntil, NextBoundSwap, NextReport;
            public int BoundTeam;
            public bool Armed;               // first weapon ready was reported
            public int Shots, Hits;
            // Defender fire at this squad, for the 15 s report (6.16.7): the
            // 6.16.6 log had no defender numbers at all.
            public int TakenShots, TakenHits;
            public bool DamageErrorLogged;
            // 6.17: the hostile vehicle in reach, and what the anti-tank
            // gunners spent on it.
            public Component Vehicle;
            public float NextVehicleScan;
            public int Drones, Rockets;
        }

        /// <summary>The dead of an ended operation, left for their loot.</summary>
        class Grave
        {
            public string Tag;
            public GameObject Settlement;
            public readonly List<GameObject> Bodies = new List<GameObject>();
            public float Until;
        }

        static readonly List<Grave> _graves = new List<Grave>();
        // Squad men by NPC_AI2 instance id, for the ApplyDamage armour prefix.
        static readonly Dictionary<int, Fighter> _armoured = new Dictionary<int, Fighter>();

        static readonly List<Squad> _squads = new List<Squad>();
        static readonly List<Fighter> _defenders = new List<Fighter>();
        static List<Component> _scene = new List<Component>();
        static float _nextSceneScan;
        static Transform _pointsRoot;
        static string _status = "";
        // One ground search per frame across all fights: each one costs a
        // handful of raycasts and a NavMesh sample per candidate.
        static int _searchBudget;

        // ------------------------------------------------------- reflection cache

        static bool _looked, _ok;
        static Type _npcType, _optType, _wpType;
        static FieldInfo _fMainOptions, _fMyFraction, _fHated, _fTempPoints, _fTempIndex, _fWpType;
        static FieldInfo _fMainWeaponId, _fSpawnWeaponId, _fShotDelayCached;
        static FieldInfo _fKillTarget, _fReloading, _fMainState, _fAddState, _fPoseState;
        static FieldInfo _fUseTemp, _fTempTaskField, _fWeapon, _fAimingPoint, _fRofDelay;
        static FieldInfo _fAimIk, _fLookTarget, _fSpecs, _fSolver, _fIkWeight;
        static FieldInfo _fHealth, _fHealthMax;
        static FieldInfo _fWeaponsManager, _fWeaponCategory, _fWeaponSlot, _fWeaponItem;
        static FieldInfo _fInitialized, _fBehavior, _fMySettlement, _fSafeSettlement;
        static MethodInfo _mEquipWeapon, _mSetMainWeaponId;
        static MethodInfo _mIsAlive, _mTempTask, _mTargetWp, _mStateSync, _mAlarm;
        static MethodInfo _mPhotonView, _mIsMine, _mMasterGetter, _mDestroy;
        static MethodInfo _mBulletsEnded, _mStartRotation, _mClearIntentions, _mPauseTime;
        static MethodInfo _mFireTo, _mHasBullets, _mMuzzle, _mCantWork;
        static MethodInfo _mStartRegen, _mIsEnemy;
        static FieldInfo _fNavAgent, _fMySpawnPoint;
        static object _wpTacticalValue;

        static bool LookUp()
        {
            if (_looked) return _ok;
            _looked = true;

            _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            _optType = RevivalPlugin.TypeByName("NPCMainOptions");
            _wpType = RevivalPlugin.TypeByName("NPC_WP");
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (_npcType == null || _optType == null || _wpType == null)
            {
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2, NPCMainOptions or NPC_WP "
                    + "not found - troop squads will not fight NPCs.");
                return false;
            }

            _fMainOptions = AccessTools.Field(_npcType, "MainOptions");
            _fTempPoints = AccessTools.Field(_npcType, "_temporaryWalkPoints");
            _fTempIndex = AccessTools.Field(_npcType, "_temporaryWalkPointIndex");
            _fMyFraction = AccessTools.Field(_optType, "MyFraction");
            _fHated = AccessTools.Field(_optType, "HatedFractions");
            _fWpType = AccessTools.Field(_wpType, "Type");
            _fKillTarget = AccessTools.Field(_npcType, "_killTarget");
            _fReloading = AccessTools.Field(_npcType, "_reloading");
            _fMainWeaponId = AccessTools.Field(_npcType, "_mainWeaponId");
            _fSpawnWeaponId = AccessTools.Field(_npcType, "_weaponId");
            _fShotDelayCached = AccessTools.Field(_npcType, "_shootingTimerDelayCached");
            if (_fShotDelayCached != null && _fShotDelayCached.FieldType != typeof(float))
                _fShotDelayCached = null;
            _fMainState = AccessTools.Field(_npcType, "MainState");
            _fAddState = AccessTools.Field(_npcType, "AdditionalState");
            _fPoseState = AccessTools.Field(_npcType, "PoseState");
            _fUseTemp = AccessTools.Field(_npcType, "_useTemporaryWalkPoints");
            _fTempTaskField = AccessTools.Field(_npcType, "TemporaryTask");
            _fWeapon = AccessTools.Field(_npcType, "_firearmWeaponController");
            _fAimingPoint = AccessTools.Field(_npcType, "_aimingPoint");
            _fAimIk = AccessTools.Field(_npcType, "_aimIk");
            _fLookTarget = AccessTools.Field(_npcType, "LookAtIKTarget");
            _fSpecs = AccessTools.Field(_npcType, "Specifications");
            _fWeaponsManager = AccessTools.Field(_npcType, "_weaponsManager");
            Type manager = RevivalPlugin.TypeByName("NPC_WeaponsManager");
            if (manager != null)
            {
                _fWeaponCategory = AccessTools.Field(manager, "WeaponCategoryEquiped");
                _fWeaponSlot = AccessTools.Field(manager, "_currentWeaponSlotId");
                _fWeaponItem = AccessTools.Field(manager, "_currentWeaponItemId");
            }
            // What NPC_AI2.ApplyDamage checks before it takes health off.
            _fInitialized = AccessTools.Field(_npcType, "IsInitialized");
            if (_fInitialized != null && _fInitialized.FieldType != typeof(bool)) _fInitialized = null;
            _fBehavior = AccessTools.Field(_npcType, "BehaviorPattern");
            _fMySettlement = AccessTools.Field(_npcType, "MySettlement");
            if (_fMySettlement != null)
            {
                _fSafeSettlement = AccessTools.Field(_fMySettlement.FieldType, "IsSafeSettlement");
                if (_fSafeSettlement != null && _fSafeSettlement.FieldType != typeof(bool))
                    _fSafeSettlement = null;
            }
            _mEquipWeapon = AccessTools.Method(_npcType, "EquipWeapon",
                new Type[] { typeof(bool), typeof(bool) }, null);
            _mSetMainWeaponId = AccessTools.Method(_npcType, "SetMainWeaponId",
                new Type[] { typeof(int), typeof(bool) }, null);

            _mIsAlive = AccessTools.Method(_npcType, "IsAlive", null, null);
            _mTempTask = AccessTools.Method(_npcType, "SetTemporaryTask", null, null);
            _mTargetWp = AccessTools.Method(_npcType, "SetTargetWalkPoint", null, null);
            _mStateSync = AccessTools.Method(_npcType, "SetStateWithAnimAndSync", null, null);
            _mAlarm = AccessTools.Method(_npcType, "SetGeneralAlarm", null, null);
            _mPhotonView = AccessTools.Method(_npcType, "get_photonView", null, null);
            _mBulletsEnded = AccessTools.Method(_npcType, "OnBulletsEnded", Type.EmptyTypes, null);
            _mStartRotation = AccessTools.Method(_npcType, "StartRotation",
                new Type[] { typeof(Vector3) }, null);
            _mClearIntentions = AccessTools.Method(_npcType, "ClearIntentions",
                Type.EmptyTypes, null);
            _mPauseTime = AccessTools.Method(_npcType, "SetPauseTime",
                new Type[] { typeof(float) }, null);
            _mStartRegen = AccessTools.Method(_npcType, "StartRegeneration", Type.EmptyTypes, null);
            _mIsEnemy = AccessTools.Method(_npcType, "IsEnemyFraction", null, null);
            if (_mIsEnemy != null && _mIsEnemy.GetParameters().Length != 1) _mIsEnemy = null;
            _fNavAgent = AccessTools.Field(_npcType, "_navAgent");
            if (_fNavAgent != null && !typeof(NavMeshAgent).IsAssignableFrom(_fNavAgent.FieldType))
                _fNavAgent = null;
            _fMySpawnPoint = AccessTools.Field(_npcType, "MySpawnPoint");

            Type weapon = _fWeapon == null ? null : _fWeapon.FieldType;
            if (weapon != null)
            {
                _mFireTo = AccessTools.Method(weapon, "FireTo",
                    new Type[] { typeof(Vector3), typeof(bool) }, null);
                _mHasBullets = AccessTools.Method(weapon, "HasBullets", Type.EmptyTypes, null);
                _mMuzzle = AccessTools.Method(weapon, "GetMuzzlePos", Type.EmptyTypes, null);
                _mCantWork = AccessTools.Method(weapon, "CantWorkWeapon", Type.EmptyTypes, null);
                _fRofDelay = AccessTools.Field(weapon, "CurrentRateOfFireDelay");
                if (_fRofDelay != null && _fRofDelay.FieldType != typeof(float)) _fRofDelay = null;
            }

            if (photon != null)
            {
                _mMasterGetter = AccessTools.PropertyGetter(photon, "isMasterClient");
                _mDestroy = AccessTools.Method(photon, "Destroy", new Type[] { typeof(GameObject) }, null);
            }

            if (_fWpType != null && _fWpType.FieldType.IsEnum)
            {
                try { _wpTacticalValue = Enum.Parse(_fWpType.FieldType, "Tactical", true); }
                catch { _wpTacticalValue = null; }
            }

            _ok = _fMainOptions != null && _fMyFraction != null && _fHated != null
                  && _mIsAlive != null;
            if (!_ok)
                RevivalPlugin.L.LogWarning("NpcWar: a required NPC_AI2 member is missing "
                    + "(MainOptions " + (_fMainOptions != null) + ", MyFraction "
                    + (_fMyFraction != null) + ", HatedFractions " + (_fHated != null)
                    + ", IsAlive " + (_mIsAlive != null) + ") - squads will not fight NPCs.");
            if (_mTempTask == null || _mTargetWp == null || _mStateSync == null
                || _fTempPoints == null || _wpTacticalValue == null)
                RevivalPlugin.L.LogWarning("NpcWar: native movement entry points missing - "
                    + "squads stay where they land.");
            if (_mFireTo == null || _mHasBullets == null || _fRofDelay == null
                || _fMainState == null || _fAddState == null)
                RevivalPlugin.L.LogWarning("NpcWar: native weapon or aim members missing - "
                    + "NPC-vs-NPC fire is disabled until a native weapon is ready.");
            if (_mEquipWeapon == null || _fWeaponCategory == null)
                RevivalPlugin.L.LogWarning("NpcWar: native equip members missing - men cannot draw weapons.");
            if (_mSetMainWeaponId == null || _fMainWeaponId == null)
                RevivalPlugin.L.LogWarning("NpcWar: native weapon-id setter missing - "
                    + "the crew cannot force a visible main weapon.");
            if (_fAimIk == null || _fLookTarget == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2._aimIk or LookAtIKTarget missing - "
                    + "the men will fire without pointing the weapon at the target.");
            if (_fInitialized == null || _fBehavior == null || _fSafeSettlement == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2 damage guards missing (IsInitialized "
                    + (_fInitialized != null) + ", BehaviorPattern " + (_fBehavior != null)
                    + ", MySettlement.IsSafeSettlement " + (_fSafeSettlement != null)
                    + ") - squads may fire at NPCs that cannot be hurt.");
            if (_mStartRegen == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2.StartRegeneration missing - a squad "
                    + "defender will not kneel and regenerate.");
            if (_fNavAgent == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2._navAgent missing - running men get "
                    + "a full move order for every change of their point.");
            if (_mIsEnemy == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2.IsEnemyFraction missing - a squad "
                    + "ignores player vehicles.");
            return _ok;
        }

        static bool IsMaster()
        {
            if (_mMasterGetter == null) return true;
            try { return (bool)_mMasterGetter.Invoke(null, null); }
            catch { return true; }
        }

        static float LineSpacing()
        {
            return Mathf.Clamp(CfgLineSpacing == null ? 8f : CfgLineSpacing.Value, 5f, 40f);
        }

        static float AssaultRange()
        {
            return Mathf.Clamp(CfgAssaultRange == null ? 180f : CfgAssaultRange.Value, 40f, 600f);
        }

        static float CloseRange()
        {
            return Mathf.Clamp(CfgCloseRange == null ? 30f : CfgCloseRange.Value, 8f, 150f);
        }

        static float BoundSeconds()
        {
            return Mathf.Clamp(CfgBoundSeconds == null ? 4f : CfgBoundSeconds.Value, 1.5f, 20f);
        }

        /// <summary>How far this man engages: the assault range for the squad,
        /// the ordinary sight range for a defender.</summary>
        static float RangeOf(Fighter f)
        {
            // 6.16.7: a defender answers at least as far as the squad fires.
            // CONFIRMED asymmetry: SightRange 110 against AssaultRange 180, and
            // the 6.16.6 squad fired with contact 46-175 units away. HYPOTHESIS
            // that it is a main reason for 3 losses of 15 (E-057). The same
            // range is the defender's accuracy falloff basis and target spread
            // scale, so both follow the longer reach.
            if (f.Squad != null && f.Class == SquadClass.Sniper) return SniperRange();
            return f.Squad != null ? AssaultRange() : Mathf.Max(CfgSightRange.Value, AssaultRange());
        }

        static float SniperRange()
        {
            return Mathf.Clamp(CfgSniperRange == null ? 380f : CfgSniperRange.Value, 60f, 900f);
        }

        static float SniperAccuracy()
        {
            return Mathf.Clamp01(CfgSniperAccuracy == null ? 0.97f : CfgSniperAccuracy.Value);
        }

        /// <summary>What one marksman's round takes compared with a rifle of the
        /// line. His weapon is the TAC-50 (SniperRifleId), so his hit is worth
        /// more than his rate of fire costs him.</summary>
        static float SniperDamage()
        {
            return Mathf.Clamp(CfgSniperDamage == null ? 2f : CfgSniperDamage.Value, 0.5f, 6f);
        }

        static float TankCloseRange()
        {
            return Mathf.Clamp(CfgTankCloseRange == null ? 10f : CfgTankCloseRange.Value, 5f, CloseRange());
        }

        static float VehicleStandoff()
        {
            return Mathf.Clamp(CfgVehicleStandoff == null ? 70f : CfgVehicleStandoff.Value,
                CloseRange(), 250f);
        }

        /// <summary>Does this man stand IN the assault line? The sniper and
        /// the anti-tank gunner fight from behind it, so the point the line
        /// walks at is measured without them (RunSquad).</summary>
        static bool InLine(Fighter f)
        {
            return f.Class != SquadClass.Sniper && f.Class != SquadClass.AntiTank;
        }

        /// <summary>How far ahead of (positive) or behind (negative) the line
        /// his class puts a man. The regular line is untouched.</summary>
        static float ClassRank(Fighter f)
        {
            switch (f.Class)
            {
                case SquadClass.Assault:
                    return Mathf.Clamp(CfgTankLead == null ? 18f : CfgTankLead.Value, 0f, 60f);
                case SquadClass.Sniper:
                    return -Mathf.Clamp(CfgSniperBack == null ? 45f : CfgSniperBack.Value, 0f, 150f);
                case SquadClass.AntiTank:
                    return -Mathf.Clamp(CfgAntiTankBack == null ? 25f : CfgAntiTankBack.Value, 0f, 150f);
                default:
                    return 0f;
            }
        }

        // ------------------------------------------------------------ operations

        /// <summary>Hand a freshly landed squad its combat arrow. The path is
        /// tail, every bend the editor drew, head - at least two points. The
        /// patrol clock starts when the line reaches the arrow head; a squad
        /// that never gets there is still removed after the walking allowance
        /// plus the patrol time, so no landing can pile men up on the map.</summary>
        internal static bool StartOperation(string tag, GameObject settlement, Array npcs,
                                            List<Vector3> path, float patrolSeconds,
                                            List<RevivalComposition.CrewMan> loadout)
        {
            if (!LookUp() || settlement == null || npcs == null) return false;
            if (path == null || path.Count < 2)
            {
                RevivalPlugin.L.LogWarning("NpcWar: operation " + tag
                    + " has no arrow (needs a tail and a head) - not started.");
                return false;
            }
            Squad s = new Squad();
            s.Tag = tag;
            s.Settlement = settlement;
            s.WalkRoot = WalkRootOf(settlement);
            s.Lz = settlement.transform.position;
            s.Path.AddRange(path);
            // ToStart is the walk to the START LINE, and it is never a walk
            // BACK. A zone that already lies down the arrow joins it where it
            // stands: the anchor is a point ON the arrow, so the line is pulled
            // onto it while it advances. Walking to the tail first put the
            // forming-up point behind the objective and then marched the squad
            // past its own landing zone again - "they gather somewhere far from
            // the target and then run off" (field report on 6.17.2).
            Vector3 down = FlatV(s.Lz - s.Tail);
            float along0 = Vector3.Dot(down, Heading(s.Path[0], s.Path[1]));
            s.Phase = along0 <= 0f && down.magnitude > StartDistance
                    ? Phase.ToStart : Phase.Advance;
            s.PatrolSeconds = Mathf.Clamp(patrolSeconds, 60f, 7200f);
            float walk = Vector3.Distance(s.Lz, s.Tail);
            for (int i = 0; i + 1 < s.Path.Count; i++)
                walk += Vector3.Distance(s.Path[i], s.Path[i + 1]);
            // One unit per second is a slow, fighting pace; plus half an hour.
            s.HardEnd = Time.time + walk + 1800f + s.PatrolSeconds;
            s.Centre = s.Lz;
            // Below any real progress, so the first frame records where the
            // squad actually starts instead of counting a walk that has not
            // reached the tail yet as a stall.
            s.Along = float.MinValue;
            s.AlongSince = Time.time;

            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                if (ai == null) continue;
                // A squad over eight men got transport firing sectors around the
                // landing zone (Crew.AssignSectors). They would pull every alarm
                // move back to the helicopter; the arrow is this squad's sector.
                CrewSector sector = ai.GetComponent<CrewSector>();
                if (sector != null) UnityEngine.Object.Destroy(sector);
                Fighter f = NewFighter(ai, s);
                f.NextMove = Time.time + 0.2f + 0.05f * s.Men.Count;
                // His loadout line is the one Crew dressed his spawn point with:
                // spawn point "Crew<i>_<role>" took loadout[i % count].
                RevivalComposition.CrewMan spec = loadout != null && loadout.Count > 0
                    ? loadout[SpawnIndex(ai, i) % loadout.Count] : null;
                Equip(f, spec);
                s.Men.Add(f);
                _armoured[ai.GetInstanceID()] = f;
            }
            if (s.Men.Count == 0) return false;
            AssignLanes(s);
            _squads.Add(s);
            EnsurePointsRoot();
            RevivalPlugin.L.LogInfo("NpcWar: operation " + tag + " - " + s.Men.Count
                + " men, arrow " + s.Tail.ToString("0") + " -> " + s.Head.ToString("0")
                + " over " + (s.Path.Count - 1) + " leg(s), " + walk.ToString("0")
                + " units to walk, "
                + (s.Phase == Phase.ToStart ? "running to the arrow first, " : "")
                + "patrol " + (s.PatrolSeconds / 60f).ToString("0") + " min"
                + (s.WalkRoot == null ? ", no walk-point root found" : "") + "; "
                + ClassSummary(s) + ".");
            return true;
        }

        /// <summary>Is an operation with this tag still on the map?</summary>
        internal static bool IsActive(string tag)
        {
            for (int i = 0; i < _squads.Count; i++)
                if (_squads[i].Tag == tag) return true;
            return false;
        }

        internal static int ActiveCount { get { return _squads.Count; } }

        internal static bool GroundOwned(Component ai)
        {
            return LookUp() && IsMine(ai);
        }

        internal static bool GroundAlive(Component ai) { return LookUp() && Alive(ai); }

        // One shared scene scan for infantry and vehicle guns, including when
        // no helicopter operation is active. Never walk the scene per vehicle.
        internal static List<Component> PatrolTargets()
        {
            if (!LookUp()) return _scene;
            if (Time.time >= _nextSceneScan)
            {
                _nextSceneScan = Time.time + 2f;
                _scene = LiveNpcs();
            }
            return _scene;
        }

        internal static bool PatrolTarget(Component ai)
        {
            return LookUp() && ai != null && ai.gameObject.activeInHierarchy
                && Alive(ai) && Targetable(ai);
        }

        internal static string PatrolFaction(Component ai)
        {
            object faction = LookUp() ? FactionOf(ai) : null;
            return faction == null ? null : faction.ToString();
        }

        internal static bool PatrolVehicleAlive(Component vehicle)
        {
            return VehicleAlive(vehicle);
        }

        internal static void RemoveGroundActor(Component ai)
        {
            if (ai != null && GroundOwned(ai)) NetDestroy(ai.gameObject);
        }

        internal static void StopGround(string tag)
        {
            for (int i = _squads.Count - 1; i >= 0; i--)
                if (_squads[i].GroundGroup && _squads[i].Tag == tag)
                    Remove(_squads[i], "ground definition changed");
        }

        internal static bool StartGround(string tag, GameObject settlement, Array npcs,
            Vector3 home, bool walking, float radius, List<RevivalComposition.CrewMan> loadout)
        {
            if (!LookUp() || settlement == null || npcs == null || IsActive(tag)) return false;
            Squad s = new Squad();
            s.Tag = tag; s.Settlement = settlement; s.Lz = home;
            s.GroundGroup = true; s.GroundWalking = walking; s.GroundRadius = radius;
            s.Centre = home; s.Front = Vector3.forward;
            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                if (ai == null || !IsMine(ai)) continue;
                CrewSector sector = ai.GetComponent<CrewSector>();
                if (sector != null) UnityEngine.Object.Destroy(sector);
                Fighter f = NewFighter(ai, s);
                f.GroundPause = Time.time + UnityEngine.Random.Range(1f, 4f);
                RevivalComposition.CrewMan spec = loadout != null && loadout.Count > 0
                    ? loadout[SpawnIndex(ai, i) % loadout.Count] : null;
                Equip(f, spec);
                s.Men.Add(f); _armoured[ai.GetInstanceID()] = f;
            }
            if (s.Men.Count == 0) return false;
            EnsurePointsRoot(); _squads.Add(s);
            RevivalPlugin.L.LogInfo("Ground enemies: " + tag + " controls " + s.Men.Count
                + " men, " + (walking ? "walking" : "waiting") + ", radius " + radius + " m.");
            return true;
        }

        static Fighter NewFighter(Component ai, Squad squad)
        {
            Fighter f = new Fighter();
            f.Ai = ai;
            f.Tr = ai.transform;
            f.Squad = squad;
            f.Faction = FactionOf(ai);
            f.Hated = GetHated(ai);
            f.WeaponId = IntField(ai, _fMainWeaponId,
                IntField(ai, _fSpawnWeaponId, 0));
            f.NextScan = Time.time + UnityEngine.Random.value * 0.3f;
            f.Skill = UnityEngine.Random.Range(0.8f, 1.2f);
            f.Nerve = UnityEngine.Random.Range(0.65f, 1.45f);
            f.Pace = UnityEngine.Random.Range(0.82f, 1.25f);
            f.NextCover = Time.time + UnityEngine.Random.value * 3f;
            return f;
        }

        /// <summary>The direction of the leg the squad is on, from a to b. A
        /// straight arrow has one leg; a bent one has a leg per drawn segment,
        /// and the squad walks them in order on the way to the head.</summary>
        static void Leg(Squad s, out Vector3 a, out Vector3 b)
        {
            int last = s.Path.Count - 2;
            int i = s.LegIndex < 0 ? 0 : (s.LegIndex > last ? last : s.LegIndex);
            switch (s.Phase)
            {
                case Phase.ToStart: a = s.Lz; b = s.Path[0]; break;
                case Phase.Advance: a = s.Path[i]; b = s.Path[i + 1]; break;
                default:
                    if (s.TowardHead) { a = s.Path[i]; b = s.Path[i + 1]; }
                    else { a = s.Path[i + 1]; b = s.Path[i]; }
                    break;
            }
        }

        static Vector3 Heading(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            d.y = 0f;
            return d.sqrMagnitude < 0.01f ? Vector3.forward : d.normalized;
        }

        /// <summary>The right-hand side of a flat direction.</summary>
        static Vector3 Side(Vector3 front)
        {
            return new Vector3(front.z, 0f, -front.x);
        }

        /// <summary>Give every living man a lane in the line across the leg he
        /// is on, in the order the men already stand from left to right, so
        /// forming up never makes two of them cross. Ten men or fewer stand in
        /// one line; a bigger squad forms a second, staggered rank.</summary>
        static void AssignLanes(Squad s)
        {
            Vector3 a, b;
            Leg(s, out a, out b);
            Vector3 side = Side(Heading(a, b));
            List<Fighter> men = new List<Fighter>();
            Vector3 centre = Vector3.zero;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                men.Add(f);
                centre += f.Tr.position;
            }
            if (men.Count == 0) return;
            centre /= men.Count;
            float[] lateral = new float[men.Count];
            for (int i = 0; i < men.Count; i++)
                lateral[i] = Vector3.Dot(men[i].Tr.position - centre, side);
            // Insertion sort keeps equal positions in list order (C# 3.0, and
            // List.Sort is not stable).
            for (int i = 1; i < men.Count; i++)
            {
                Fighter fm = men[i];
                float fl = lateral[i];
                int k = i - 1;
                while (k >= 0 && lateral[k] > fl)
                {
                    men[k + 1] = men[k];
                    lateral[k + 1] = lateral[k];
                    k--;
                }
                men[k + 1] = fm;
                lateral[k + 1] = fl;
            }
            LayOut(men.Count, LineSpacing(), men);
        }

        /// <summary>Lane numbers to offsets: index k of n, left to right.
        /// 6.19: a landing may put 24 men down (RevivalTroopInsertion.MaxSquad),
        /// and 24 in two ranks is a front 92 units wide - wider than the
        /// settlement it attacks. Over twenty men take a third rank instead, so
        /// the frontage stays about what sixteen had and neighbours in one rank
        /// keep their LineSpacing.</summary>
        static void LayOut(int n, float spacing, List<Fighter> men)
        {
            int ranks = n <= PerRank ? 1 : (n <= 2 * PerRank ? 2 : 3);
            float step = spacing / ranks;
            for (int k = 0; k < n; k++)
            {
                Fighter f = men[k];
                f.LaneOffset = (k - (n - 1) * 0.5f) * step;
                f.RankOffset = -(k % ranks) * RankDepth + ClassRank(f);
                f.Team = k % 2;
            }
        }

        static Transform WalkRootOf(GameObject settlement)
        {
            try
            {
                Type sType = RevivalPlugin.TypeByName("NPC_Settlement");
                Component sied = sType == null ? null : settlement.GetComponent(sType);
                FieldInfo f = sied == null ? null : AccessTools.Field(sType, "AllWalkPointsTr");
                return f == null ? null : f.GetValue(sied) as Transform;
            }
            catch { return null; }
        }

        // ----------------------------------------------------------- per frame

        public static void Tick()
        {
            if (_squads.Count == 0 && _defenders.Count == 0 && _graves.Count == 0)
            { _status = ""; return; }
            if (!LookUp()) return;
            if (!IsMaster()) { _status = "NpcWar: not master client"; return; }

            float now = Time.time;
            if (_graves.Count > 0) TickGraves(now);
            _searchBudget = 1;
            PatrolTargets();

            for (int q = _squads.Count - 1; q >= 0; q--)
            {
                Squad s = _squads[q];
                try { RunSquad(s, now); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("NpcWar: operation " + s.Tag + " - " + ex);
                    Remove(s, "error");
                }
            }

            for (int i = _defenders.Count - 1; i >= 0; i--)
            {
                Fighter d = _defenders[i];
                bool gone = d.Ai == null || d.Tr == null || !Alive(d.Ai);
                // A defender with no squad man anywhere near, or who can no
                // longer be hurt (a talk started, god mode), leaves the fight.
                // Twice a second is plenty: with 32 defenders and 15 men the
                // distance test alone was 480 reflective IsAlive calls a frame.
                if (!gone && now >= d.NextTargetCheck)
                {
                    d.NextTargetCheck = now + 0.5f;
                    gone = NearestSquadMan(d, 2f) == null || !Targetable(d.Ai);
                }
                if (gone)
                {
                    if (d.Ai != null && Alive(d.Ai)
                        && (d.WantAdd == AddAim || d.WantAdd == AddFire)) StandDown(d);
                    _defenders.RemoveAt(i);
                    continue;
                }
                if (HasKillTarget(d))
                {
                    if (d.IkDriven) ReleaseAim(d);
                    continue;   // a player: the game's fight
                }
                try { RunDefender(d, now); }
                catch (Exception ex)
                {
                    if (CfgDebug.Value)
                        RevivalPlugin.L.LogWarning("NpcWar: defender - " + ex.Message);
                    _defenders.RemoveAt(i);
                }
            }

            _status = "NpcWar: " + _squads.Count + " operation(s), "
                + _defenders.Count + " defender(s)" + DebugTail();
        }

        static void RunSquad(Squad s, float now)
        {
            if (s.GroundGroup) { RunGround(s, now); return; }
            int alive = 0, inLine = 0;
            Vector3 centre = Vector3.zero, lineSum = Vector3.zero, front = Vector3.zero;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                alive++;
                centre += f.Tr.position;
                if (InLine(f)) { inLine++; lineSum += f.Tr.position; }
            }

            if (s.Settlement == null) { Remove(s, "settlement gone"); return; }
            if (alive == 0) { Remove(s, "wiped out"); return; }
            if (now >= s.HardEnd) { Remove(s, "time is up"); return; }
            if (s.Phase == Phase.Patrol && now >= s.PatrolEnds) { Remove(s, "patrol over"); return; }
            centre /= alive;
            s.Centre = centre;
            // Everything about the ADVANCE is measured on the assault line, not
            // on the average of everybody. The sniper and the anti-tank gunner
            // fight from behind the line, so every step they did not take moved
            // the line's own point back: three snipers who stayed at the
            // landing zone stopped the 6.17.1 squad dead (E-060).
            Vector3 line = inLine > 0 ? lineSum / inLine : centre;
            s.Line = line;

            // The ring of walk and tactical points follows the body, so whatever
            // the vanilla alarm picks between two orders is next to the squad,
            // not back at the landing zone. Never the settlement itself: the
            // men are its children (InitSpawnNpc) and would be dragged along.
            if (s.WalkRoot != null && now >= s.NextRing)
            {
                s.NextRing = now + 2f;
                s.WalkRoot.position = centre;
            }

            Vector3 a, b;
            Leg(s, out a, out b);
            Vector3 dir = Heading(a, b);
            float len = Flat(b - a);
            float along = Vector3.Dot(FlatV(line - a), dir);
            if (along >= len - Arrive)
            {
                int last = s.Path.Count - 2;
                if (s.Phase == Phase.ToStart) { s.Phase = Phase.Advance; s.LegIndex = 0; }
                else if (s.Phase == Phase.Advance)
                {
                    if (s.LegIndex < last)
                    {
                        s.LegIndex++;
                        RevivalPlugin.L.LogInfo("NpcWar: operation " + s.Tag + " turned at bend "
                            + s.LegIndex + " of " + last + " with " + alive + " men.");
                    }
                    else
                    {
                        s.Phase = Phase.Patrol;
                        s.PatrolEnds = now + s.PatrolSeconds;
                        s.TowardHead = false;
                        RevivalPlugin.L.LogInfo("NpcWar: operation " + s.Tag + " reached the "
                            + "arrow head with " + alive + " men - patrolling for "
                            + (s.PatrolSeconds / 60f).ToString("0") + " min.");
                    }
                }
                else if (s.TowardHead)
                {
                    if (s.LegIndex < last) s.LegIndex++; else s.TowardHead = false;
                }
                else
                {
                    if (s.LegIndex > 0) s.LegIndex--; else s.TowardHead = true;
                }
                AssignLanes(s);
                Leg(s, out a, out b);
                dir = Heading(a, b);
                len = Flat(b - a);
                along = Vector3.Dot(FlatV(line - a), dir);
                s.Along = along;
                s.AlongSince = now;
            }
            // The advance watchdog. A line that is walking gains ground on its
            // leg; one that gains none for StallSeconds is stuck, and that is
            // the whole "they never arrive" of the field report. Two floats a
            // frame here, one log line every half minute in Report.
            s.LegLen = len;
            if (along > s.Along + 2f) { s.Along = along; s.AlongSince = now; }
            else if (s.AlongSince <= 0f) s.AlongSince = now;

            // A hostile vehicle in reach (6.17), twice a second.
            if (now >= s.NextVehicleScan)
            {
                s.NextVehicleScan = now + 0.5f;
                s.Vehicle = HostileVehicle(s, centre);
            }

            // The contact picture: who is the squad fighting? Infantry and
            // players first; the vehicle when there is nobody else.
            if (now >= s.NextThreat)
            {
                s.NextThreat = now + 0.25f;
                bool seen;
                Transform enemy = SquadThreat(s, centre, now, out seen);
                if (enemy == null && s.Vehicle != null)
                {
                    enemy = s.Vehicle.transform;
                    seen = VehicleClear(centre + Vector3.up * EyeHeight,
                                        enemy.position + Vector3.up * VehicleAim, s.Vehicle);
                }
                if (enemy != null) { s.Threat = enemy; s.ThreatSeen = seen; s.ThreatUntil = now + 6f; }
                else if (now >= s.ThreatUntil) s.Threat = null;
                else s.ThreatSeen = false;
            }
            if (s.Threat != null && !s.Threat) s.Threat = null;
            // An enemy somebody can shoot at is closed to CloseRange and fought
            // from there. One that nobody can see is walked up to until
            // somebody can - a line that stops at a wall wins nothing. A
            // vehicle is fought from VehicleStandoff.
            bool vehicleFight = s.Vehicle != null && s.Threat != null && s.Threat == s.Vehicle.transform;
            float stopAt = vehicleFight ? VehicleStandoff() : (s.ThreatSeen ? CloseRange() : 8f);

            // The line faces the enemy while there is one in front of it;
            // otherwise it faces along the arrow.
            front = dir;
            float threatDist = 0f;
            if (s.Threat != null)
            {
                Vector3 to = FlatV(s.Threat.position - line);
                threatDist = to.magnitude;
                if (threatDist > 1f && Vector3.Angle(to, dir) <= 100f) front = to / threatDist;
                if (now >= s.NextBoundSwap)
                {
                    s.NextBoundSwap = now + BoundSeconds();
                    s.BoundTeam = 1 - s.BoundTeam;
                }
            }
            s.Front = front;

            Vector3 anchor = Anchor(a, dir, len, line, front, s.Threat != null,
                                    threatDist, stopAt);

            if (!s.Armed)
            {
                int armed = 0;
                for (int i = 0; i < s.Men.Count; i++) if (s.Men[i].Armed) armed++;
                if (armed > 0)
                {
                    s.Armed = true;
                    RevivalPlugin.L.LogInfo("NpcWar: operation " + s.Tag + " - weapons in hand ("
                        + armed + "/" + alive + ").");
                }
            }

            // Performance, spent where it cannot cost anything. With no contact
            // and no player within FarTick this squad is a column of men walking
            // a line that nobody is looking at; their NavMeshAgents keep walking
            // between our orders, so stepping a third of them per frame changes
            // what they do not at all and costs a third. One contact, or one
            // player in sight, and everybody is stepped every frame again.
            if (now >= s.NextQuiet)
            {
                s.NextQuiet = now + 0.5f;
                s.Quiet = !PlayerNear(centre, FarTick);
            }
            int slice = s.Quiet && s.Threat == null ? 3 : 1;
            if (slice > 1) s.StepOffset = (s.StepOffset + 1) % slice;
            else s.StepOffset = 0;

            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                if (slice > 1 && i % slice != s.StepOffset) continue;
                // One man's failure costs his turn, never the whole operation,
                // and it is never silent: the first failure and every 600th
                // after it (about ten seconds) go to the log with the stack.
                try { ManStep(f, s, anchor, front, line, now); f.StepFailures = 0; }
                catch (Exception ex)
                {
                    if (f.StepFailures++ % 600 == 0)
                        RevivalPlugin.L.LogWarning("NpcWar: " + s.Tag + " man step failed ("
                            + f.StepFailures + "x) - " + ex);
                }
            }

            if (now >= s.NextReport) Report(s, now, alive);
        }

        // Ground groups share weapons, faction targeting, damage, replication
        // and corpse cleanup with troop squads, but never issue assault orders.
        static void RunGround(Squad s, float now)
        {
            if (s.Settlement == null) { Remove(s, "settlement gone"); return; }
            if (now >= s.NextVehicleScan)
            {
                s.NextVehicleScan = now + 0.5f;
                s.Vehicle = HostileVehicle(s, s.Settlement.transform.position);
            }
            int alive = 0;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) continue;
                alive++;
                if (!IsMine(f.Ai)) continue;
                if (Regenerating(f, now)) continue;
                EnsureArmed(f, now); Acquire(f, now); Planted(f, now);
                if (Reloading(f)) { Quiet(f, true); continue; }
                if (f.Target != null && f.Sees && f.Armed
                    && Flat(f.Target.position - f.Tr.position) <= RangeOf(f))
                {
                    Fire(f, now);
                    f.GroundPause = now + 2f;
                    continue;
                }
                // Waiting men stand where placed. No target pursuit or running
                // is allowed for either behavior, including during combat.
                if (!s.GroundWalking) { Hold(f, null, now); continue; }
                if (f.HasOrder)
                {
                    if (Flat(f.Tr.position - f.Ordered) <= 2f || now >= f.MoveDeadline)
                    {
                        f.HasOrder = false;
                        f.GroundPause = now + UnityEngine.Random.Range(2f, 6f);
                    }
                    else
                    {
                        // Keep the native alarm from switching a walk to a run.
                        Drive(f, MainWalk, AddNone, PoseStand, now, false);
                        continue;
                    }
                }
                if (now < f.GroundPause) { Hold(f, null, now); continue; }
                f.GroundPause = now + 5f;
                Vector3 dest;
                if (!GroundDestination(f, s, out dest)) { Hold(f, null, now); continue; }
                Go(f, dest, MainWalk, PoseStand, now, Stance.Advance);
                f.MoveDeadline = now + 15f + Flat(dest - f.Tr.position) / 0.8f;
            }
            if (alive == 0) Remove(s, "ground group defeated");
        }

        static bool GroundDestination(Fighter f, Squad s, out Vector3 dest)
        {
            dest = s.Lz;
            NavMeshAgent agent = Agent(f);
            if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return false;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Vector2 offset = UnityEngine.Random.insideUnitCircle * (s.GroundRadius - 3f);
                Vector3 candidate = s.Lz + new Vector3(offset.x, 0f, offset.y);
                if (!RevivalGroundEnemies.TryGround(candidate, 6f, out dest)
                    || Flat(dest - s.Lz) > s.GroundRadius || Flat(dest - f.Tr.position) < 5f) continue;
                NavMeshPath path = new NavMeshPath();
                if (!NavMesh.CalculatePath(f.Tr.position, dest, agent.areaMask, path)
                    || path.status != NavMeshPathStatus.PathComplete) continue;
                bool inside = true;
                Vector3[] corners = path.corners;
                for (int c = 0; c < corners.Length; c++)
                    if (Flat(corners[c] - s.Lz) > s.GroundRadius) { inside = false; break; }
                if (inside) return true;
            }
            return false;
        }

        /// <summary>The point the whole line runs at. Out of contact, and with
        /// an enemy straight down the arrow, it lies ON the arrow Lead units
        /// ahead of the line's centre, never past the leg's end - so a squad
        /// that landed beside the arrow is pulled onto it while it advances.
        /// With an enemy off the arrow it lies toward that enemy. Never closer
        /// than stopAt to the enemy, and never further from the drawn leg than
        /// the corridor (6.18).</summary>
        static Vector3 Anchor(Vector3 a, Vector3 dir, float len, Vector3 centre, Vector3 front,
                             bool contact, float threatDist, float stopAt)
        {
            float room = Mathf.Max(0f, threatDist - stopAt);
            if (contact && Vector3.Angle(front, dir) > 1f)
            {
                // Toward the enemy - but never back down the arrow. A squad
                // shot at from behind turns and fights where it stands; it does
                // not walk back to its landing zone (6.17.1 field report).
                Vector3 point = centre + front * Mathf.Min(Lead, room);
                float back = Vector3.Dot(FlatV(point - centre), dir);
                if (back < 0f) point -= dir * back;
                return Corridor(a, dir, point);
            }
            float along = Vector3.Dot(FlatV(centre - a), dir);
            float step = Mathf.Min(len, Mathf.Max(0f, along) + Lead) - along;
            if (contact) step = Mathf.Min(step, room);
            return a + dir * (along + Mathf.Max(0f, step));
        }

        /// <summary>The arrow is the line's spine, not a suggestion. An enemy
        /// beside it may pull the line's point off the drawn leg, but only this
        /// far sideways; past that the point is put back into the corridor and
        /// the advance carries on toward the head. Without the cap every
        /// contact off the arrow moved the whole line after it, one Lead at a
        /// time, and the squad ended up somewhere it had no business being -
        /// the 6.17.2 field report. ArrowCorridor 0 keeps it exactly on the
        /// arrow.</summary>
        static Vector3 Corridor(Vector3 a, Vector3 dir, Vector3 point)
        {
            float wide = Mathf.Clamp(CfgArrowCorridor == null ? 60f : CfgArrowCorridor.Value,
                                     0f, 400f);
            Vector3 side = Side(dir);
            float off = Vector3.Dot(FlatV(point - a), side);
            if (off > wide) point -= side * (off - wide);
            else if (off < -wide) point -= side * (off + wide);
            return point;
        }

        /// <summary>The enemy the squad as a whole is fighting: the nearest one
        /// any man has seen in the last few seconds, or the player a man's own
        /// AI is hunting. An enemy just round a corner that nobody sees yet
        /// still counts when it is close, so the line pushes to it instead of
        /// marching past.</summary>
        static Transform SquadThreat(Squad s, Vector3 centre, float now, out bool seen)
        {
            Transform best = null;
            seen = false;
            float bestSqr = AssaultRange() * AssaultRange();
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                if (f.Target != null && f.Target && f.Sees && now - f.LastSeen < 1f) seen = true;
                Transform t = null;
                if (f.Target != null && f.Target && now - f.LastSeen < 2.5f) t = f.Target;
                Component player = KillTarget(f);
                if (player != null)
                {
                    float pd = (player.transform.position - centre).sqrMagnitude;
                    if (pd < bestSqr) { best = player.transform; bestSqr = pd; }
                }
                if (t == null) continue;
                float d = (t.position - centre).sqrMagnitude;
                if (d < bestSqr) { best = t; bestSqr = d; }
            }
            if (best != null) return best;

            float near = CloseRange() * 2.5f;
            bestSqr = near * near;
            for (int i = 0; i < _scene.Count; i++)
            {
                Component c = _scene[i];
                if (c == null || !Alive(c)) continue;
                Fighter other = FighterOf(c);
                if (other != null && other.Squad == s) continue;
                if ((other == null || other.Squad == null) && !Targetable(c)) continue;
                float d = (c.transform.position - centre).sqrMagnitude;
                if (d >= bestSqr) continue;
                if (!HatedBySquad(s, c)) continue;
                best = c.transform; bestSqr = d;
            }
            return best;
        }

        static bool HatedBySquad(Squad s, Component c)
        {
            object faction = FactionOf(c);
            for (int m = 0; m < s.Men.Count; m++)
            {
                Fighter man = s.Men[m];
                if (man.Ai != null && Hostile(man.Hated, faction)) return true;
            }
            return false;
        }

        /// <summary>A compact line in the runtime log every fifteen seconds, so
        /// a field report can be read out of LogOutput.log without Debug.</summary>
        static void Report(Squad s, float now, int alive)
        {
            s.NextReport = now + 15f;
            int armed = 0, fire = 0, move = 0, hold = 0, reload = 0, kneel = 0;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                if (f.Armed) armed++;
                if (IntField(f.Ai, _fMainState, -1) == MainRegen) kneel++;
                else if (Reloading(f)) reload++;
                else if (f.Stance == Stance.Fire) fire++;
                else if (f.Stance == Stance.Hold) hold++;
                else move++;
            }
            RevivalPlugin.L.LogInfo("NpcWar: " + s.Tag + " " + s.Phase + " - " + alive + " alive, "
                + armed + " armed, " + fire + " firing, " + move + " moving, " + hold + " holding, "
                + reload + " reloading, " + kneel + " kneeling; " + s.Shots + " shots at NPCs, "
                + s.Hits + " hits; "
                + _defenders.Count + " defender(s) enlisted, " + s.TakenShots + " shots at the squad, "
                + s.TakenHits + " hits; "
                + (s.Threat == null ? "no contact"
                   : "contact " + Flat(s.Threat.position - s.Centre).ToString("0") + " units away")
                + (s.Vehicle == null ? "" : ", hostile vehicle "
                   + Flat(s.Vehicle.transform.position - s.Centre).ToString("0") + " units away")
                + ", " + s.Drones + " drone(s), " + s.Rockets + " LAW rocket(s)"
                + ", leg " + (s.LegIndex + 1) + "/" + Mathf.Max(1, s.Path.Count - 1)
                + " at " + s.Along.ToString("0") + "/" + s.LegLen.ToString("0")
                + (s.Quiet ? ", nobody near" : "")
                + ", centre " + s.Centre.ToString("0")
                + ", line " + s.Line.ToString("0") + ".");
            if (s.Phase != Phase.Patrol && s.Threat == null
                && now - s.AlongSince > StallSeconds && now >= s.NextStall)
                Stalled(s, now);
        }

        /// <summary>The line has gained no ground on its leg for half a minute
        /// and has nobody to fight. Say so once every half minute, with the one
        /// number that decides it: how many men the NavMesh has lost. A squad
        /// dropped far from every player walks on ground whose colliders are
        /// switched off, and a man whose agent is not on the mesh will stand
        /// there until the world ends (field report, 6.17.2).</summary>
        static void Stalled(Squad s, float now)
        {
            s.NextStall = now + StallSeconds;
            int off = 0, idle = 0, pathless = 0, unstuck = 0, live = 0;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                live++;
                unstuck += f.Unstuck;
                if (IntField(f.Ai, _fMainState, -1) == MainIdle) idle++;
                NavMeshAgent a = Agent(f);
                if (a == null || !a.isActiveAndEnabled) continue;
                if (!a.isOnNavMesh) off++;
                else if (!a.hasPath && !a.pathPending) pathless++;
            }
            RevivalPlugin.L.LogWarning("NpcWar: " + s.Tag + " has not gained ground for "
                + (now - s.AlongSince).ToString("0") + " s on leg " + (s.LegIndex + 1)
                + "/" + Mathf.Max(1, s.Path.Count - 1) + " (" + s.Along.ToString("0")
                + "/" + s.LegLen.ToString("0") + ") - " + live + " men, " + off
                + " off the NavMesh, " + pathless + " without a path, " + idle
                + " idle, " + unstuck + " unstick attempt(s); line "
                + s.Line.ToString("0") + ".");
        }

        // -------------------------------------------------------- one man's turn

        /// <summary>One squad man: see, then shoot, bound or run on with the
        /// line. Nothing here moves a man sideways away from the fight.</summary>
        static void ManStep(Fighter f, Squad s, Vector3 anchor, Vector3 front,
                            Vector3 centre, float now)
        {
            // A defender kneeling in the game's own regeneration belongs to the
            // game: RegenerationActions heals him and ends the state, and any
            // order of ours - even the pause Quiet refreshes - would cut it short.
            if (Regenerating(f, now))
            {
                f.Stance = Stance.Hold;
                f.HasOrder = false;
                if (f.IkDriven) ReleaseAim(f);
                return;
            }
            // The anti-tank gunner putting one weapon away and drawing the
            // other: no draw repair of ours in between, and no shot.
            bool switching = WeaponSwitch(f, now);
            if (switching) f.Armed = false;
            else EnsureArmed(f, now);
            Acquire(f, now);
            // Sampled every frame, so a pass through a run between two shots
            // is never missed (Planted).
            Planted(f, now);
            if (RegenDue(f, now) && StartRegen(f, now)) return;
            if (f.Class == SquadClass.AntiTank && AntiTankStep(f, s, front, centre, now)) return;

            // A barrel behind a wall, or a comrade in the line of fire: the eyes
            // may see, the rifle may not. Run on with the line for a moment
            // instead of standing there - forward, never sideways.
            if ((f.MuzzleBlockedSince > 0f && now - f.MuzzleBlockedSince > 0.8f)
                || (f.MateBlockedSince > 0f && now - f.MateBlockedSince > 1.0f))
            {
                f.BlindUntil = now + 2f;
                f.MuzzleBlockedSince = 0f;
                f.MateBlockedSince = 0f;
            }

            // The reload is the game's own animation and state.
            if (Reloading(f))
            {
                f.Stance = Stance.Hold;
                if (!f.TargetIsPlayer && f.IkDriven) ReleaseAim(f);
                Quiet(f, true);
                Face(f);
                return;
            }

            // 1  A bound in progress: finish it, then fire again at once.
            if (f.Stance == Stance.Bound && f.HasOrder)
            {
                if (now < f.BoundUntil && Flat(f.Tr.position - f.BoundDest) > LaneSlack)
                {
                    Drive(f, MainRun, AddNone, PoseStand, now, false);
                    return;
                }
                f.HasOrder = false;
                f.Stance = Stance.Fire;
                f.FireSince = now;
            }

            // How far ahead of (positive) or behind (negative) his place in
            // the line he stands. A man more than Catchup behind it closes up
            // before he fires again - forward always. The 6.17.1 line froze
            // because its three snipers stood at the landing zone firing at a
            // BTR 250 units away, which only their range reached, while the
            // rest of the line walked off without them (E-060). An enemy
            // inside close range is the exception: nobody runs past a man who
            // is already shooting at him.
            float ahead = Vector3.Dot(FlatV(f.Tr.position - centre), front) - f.RankOffset;

            // 2  A visible enemy in range and a weapon in hand: shoot now.
            bool sees = f.Target != null && f.Sees && now >= f.BlindUntil;
            float dist = f.Target == null ? 0f : Flat(f.Target.position - f.Tr.position);
            float range = RangeOf(f);
            if (sees && f.Armed && dist <= range
                && (ahead > -Catchup || dist <= CloseRange()))
            {
                if (f.FireSince <= 0f)
                {
                    f.FireSince = now;
                    // A sniper takes his time over the first round.
                    if (f.Class == SquadClass.Sniper) f.NextShot = Mathf.Max(f.NextShot, now + 1f);
                }
                f.SteadyUntil = now + (f.Class == SquadClass.Sniper ? SteadySeconds * 2f : SteadySeconds);
                // The anti-tank gunner keeps out of the gunfight: his rifle
                // only answers an enemy close to him.
                if (f.Class == SquadClass.AntiTank && dist > AntiTankSelfDefense())
                {
                    Steady(f, now);
                    return;
                }
                if (BoundDue(f, s, front, dist, now))
                {
                    StartBound(f, s, front, centre, now);
                    return;
                }
                Fire(f, now);
                return;
            }

            // 2b The target has only just gone out of sight. A man who breaks
            //    into a run for every glimpse he loses never plants his feet:
            //    he stands in the aim clip for a moment and fires again the
            //    instant it shows. No line of fire at all (BlindUntil) still
            //    sends him on at once, and the tank never waits.
            if (f.Armed && f.Target != null && now < f.SteadyUntil && now >= f.BlindUntil
                && dist <= range && ahead > -Catchup && f.Class != SquadClass.Assault)
            {
                Steady(f, now);
                return;
            }
            f.FireSince = 0f;

            // 3  Otherwise run on with the line - his class decides where in it
            //    (LayOut); the tank goes no closer than TankCloseRange.
            Vector3 dest = anchor + Side(front) * f.LaneOffset + front * f.RankOffset;
            if (f.Class == SquadClass.Assault && s.Threat != null)
                dest = KeepOff(dest, s.Threat.position, front, TankCloseRange());
            MoveInLine(f, s, dest, front, centre, now);
        }

        /// <summary>A destination no closer than minDist to a point, pulled
        /// straight back from it.</summary>
        static Vector3 KeepOff(Vector3 dest, Vector3 threat, Vector3 front, float minDist)
        {
            Vector3 away = FlatV(dest - threat);
            float d = away.magnitude;
            if (d >= minDist) return dest;
            if (d < 0.01f) away = front * -1f;
            else away /= d;
            return new Vector3(threat.x, dest.y, threat.z) + away * minDist;
        }

        /// <summary>His half of the squad is the one that moves now, he has
        /// already fired for a moment, and the enemy is still well beyond close
        /// range and in front of the line.</summary>
        static bool BoundDue(Fighter f, Squad s, Vector3 front, float dist, float now)
        {
            // The sniper and the anti-tank gunner fight from where they stand,
            // and nobody bounds at a vehicle.
            if (f.Class == SquadClass.Sniper || f.Class == SquadClass.AntiTank) return false;
            if (s.Threat == null || now < f.NextBound) return false;
            if (s.Vehicle != null && s.Threat == s.Vehicle.transform) return false;
            if (f.Class == SquadClass.Assault)
            {
                // The tank goes in on his own rhythm, not with a half of the line.
                if (now - f.FireSince < 1.2f || dist <= TankCloseRange() + BoundStep * 0.5f) return false;
            }
            else
            {
                if (s.BoundTeam != f.Team) return false;
                if (now - f.FireSince < 1.5f || dist <= CloseRange() + BoundStep) return false;
            }
            Vector3 to = FlatV(f.Target.position - f.Tr.position);
            return Vector3.Angle(to, front) <= 60f;
        }

        /// <summary>One bound forward, pulled a little toward his lane.</summary>
        static void StartBound(Fighter f, Squad s, Vector3 front, Vector3 centre, float now)
        {
            Vector3 side = Side(front);
            float drift = Vector3.Dot(FlatV(centre + side * f.LaneOffset - f.Tr.position), side);
            Vector3 dest = f.Tr.position + front * BoundStep + side * Mathf.Clamp(drift, -6f, 6f);
            float close = f.Class == SquadClass.Assault ? TankCloseRange() : CloseRange();
            if (f.Target != null)
            {
                float after = Flat(f.Target.position - dest);
                if (after < close)
                    dest -= front * Mathf.Min(BoundStep, close - after);
            }
            f.BoundDest = dest;
            f.BoundUntil = now + 4f;
            f.NextBound = now + (f.Class == SquadClass.Assault ? 2.5f : BoundSeconds() * 1.5f);
            f.FireSince = 0f;
            Go(f, dest, MainRun, PoseStand, now, Stance.Bound);
        }

        /// <summary>Run to his place in the line. The place is Lead units ahead
        /// of the line, so while there is ground to take he never arrives; a
        /// man who has got ahead of the others walks until they are with him
        /// again.</summary>
        static void MoveInLine(Fighter f, Squad s, Vector3 dest, Vector3 front,
                               Vector3 centre, float now)
        {
            // Never back: a man who bounded past the point where the line
            // stops, or who is ahead of it at the arrow head, only steps
            // sideways into his lane and waits there for the others.
            float back = Vector3.Dot(FlatV(dest - f.Tr.position), front);
            if (back < 0f) dest -= front * back;
            float away = Flat(dest - f.Tr.position);
            if (away <= LaneSlack)
            {
                Hold(f, s, now);
                return;
            }
            // He is meant to be covering ground. If he is not, do something
            // about it before the whole line waits for him.
            Unstick(f, dest, now);
            float ahead = Vector3.Dot(FlatV(f.Tr.position - centre), front) - f.RankOffset;
            bool walking = f.Stance == Stance.Advance && f.WantMain == MainWalk;
            int state = MainRun;
            if (s.Threat == null && (ahead > AheadWalk || (walking && ahead > AheadRun)))
                state = MainWalk;
            if (away < 10f) state = MainWalk;

            if (f.IkDriven) ReleaseAim(f);
            f.Stance = Stance.Advance;
            int live = IntField(f.Ai, _fMainState, -1);
            // Every full order is a SetStateWithAnimAndSync RPC to every player
            // around, and on this machine NavAgentMoveToPos stops the man
            // before it gives him the new path (6.17). While he runs to our
            // point, a point that barely moved is left alone and one that
            // moved on is re-aimed on his NavMeshAgent (Retarget); a full order
            // goes out at most every FullOrderSeconds, or when he turns.
            if (f.HasOrder && live == state && StillOurPoint(f) && now < f.MoveDeadline)
            {
                if (Flat(dest - f.Ordered) < RetargetSlack) return;
                if (now - f.LastFullOrder < FullOrderSeconds
                    && Vector3.Angle(FlatV(dest - f.Tr.position), FlatV(f.Ordered - f.Tr.position)) <= RetargetAngle
                    && Retarget(f, dest, now))
                    return;
            }
            // The game ended his path (RunStateAction -> Idle) while there is
            // still ground to take: send him on at once instead of letting him
            // stand for the rest of the second.
            bool stopped = f.HasOrder && live == MainIdle && now - f.LastFullOrder > 0.6f;
            // The settlement alarm replaces the temporary walk list with the
            // settlement's own tactical points (IntentionsActions IL_044F).
            // Until now we noticed and then waited out the rest of the second -
            // and for that second the man ran wherever the game had sent him.
            // That is the "and then they run somewhere else" of the field
            // report; a stolen point is taken back at once, at most three
            // times a second so a missing reflection field cannot spam RPCs.
            bool stolen = f.HasOrder && now - f.LastFullOrder > 0.3f && !StillOurPoint(f);
            if (now < f.NextMove && !stopped && !stolen) return;
            f.NextMove = now + 1f;
            f.LastFullOrder = now;
            OrderMove(f, dest, state, AddNone, PoseStand);
        }

        /// <summary>
        /// A man who was told to run and is not covering ground. One distance
        /// compare a frame; it does something at most once every StuckSeconds.
        ///
        /// WHY THIS EXISTS. A landing zone far from every player is ground
        /// whose whole-map TerrainColliders are switched off at runtime - the
        /// same finding that made the helicopter miss its own zone in 6.17.1
        /// (E-059). A walk point built from a downward ray then keeps the
        /// arrow's own height instead of the hill's, the NavMesh sample beside
        /// it misses, and the man stands where he was set down for good. The
        /// player teleports over, the colliders come back, and it looks as if
        /// the squad only ever moves when he is watching. It has to stop, and
        /// it costs nothing when nothing is wrong.
        ///
        /// Three steps, in order of how visible they are:
        ///   1  a fresh full order - he may simply have finished his path
        ///   2  his agent back onto the NavMesh where he stands - not a move,
        ///      just the half metre that puts him back on the mesh
        ///   3  his place in the line, and only with no player within
        ///      RescueQuiet, so nobody ever sees a man jump
        /// </summary>
        static void Unstick(Fighter f, Vector3 dest, float now)
        {
            float limit = CfgStuckSeconds == null ? 6f : CfgStuckSeconds.Value;
            if (limit <= 0f) return;
            // The clock only runs while he is walking. A man who has been
            // firing, bounding or holding stood still because that was his job,
            // and MoveInLine sets Stance to Advance only after this call - so
            // what is read here is what he did last frame.
            if (f.Stance != Stance.Advance
                || f.MovedAt <= 0f || Flat(f.Tr.position - f.LastPos) > StuckMove)
            {
                f.LastPos = f.Tr.position;
                f.MovedAt = now;
                f.Unstuck = 0;
                return;
            }
            if (now - f.MovedAt < limit || now < f.NextUnstuck) return;
            f.NextUnstuck = now + limit;
            f.Unstuck++;

            NavMeshAgent a = Agent(f);
            bool usable = a != null && a.isActiveAndEnabled;
            bool offMesh = usable && !a.isOnNavMesh;
            if (f.Unstuck == 1 && !offMesh)
            {
                // Most of the time this is all it takes.
                f.NextMove = 0f;
                f.HasOrder = false;
                return;
            }

            bool rescue = f.Unstuck >= 3 && !PlayerNear(f.Tr.position, RescueQuiet);
            Vector3 spot = Ground(rescue ? dest : f.Tr.position);
            try
            {
                if (usable) a.Warp(spot);
                else f.Tr.position = spot;
            }
            catch { }
            f.LastPos = f.Tr.position;
            f.MovedAt = now;
            f.NextMove = 0f;
            f.HasOrder = false;
            // Loud the first few times and then every tenth: a man nothing can
            // free must not drown the log a squad report is read out of.
            if (f.Unstuck > 3 && f.Unstuck % 10 != 0) return;
            RevivalPlugin.L.LogWarning("NpcWar: " + (f.Squad == null ? "?" : f.Squad.Tag)
                + " - " + f.Ai.name + " stood still for " + limit.ToString("0") + " s ("
                + (a == null ? "no agent" : (offMesh ? "off the NavMesh" : "on the NavMesh"))
                + ", attempt " + f.Unstuck + ") - "
                + (rescue ? "put back into the line at " : "put back on the mesh at ")
                + spot.ToString("0") + ".");
        }

        /// <summary>At his place (the arrow head, or close range): stand, and
        /// keep the weapon up toward the enemy while there is one.</summary>
        static void Hold(Fighter f, Squad s, float now)
        {
            f.Stance = Stance.Hold;
            f.HasOrder = false;
            if (f.Armed && s != null && s.Threat != null)
            {
                Transform keep = f.Target;
                if (f.Target == null)
                {
                    // Only an enemy he could hit is worth standing and aiming
                    // at. The 6.17.1 line held its weapons on a vehicle a
                    // quarter of a kilometre away that only its snipers could
                    // reach - and turned its back on the arrow to do it.
                    if (Flat(s.Threat.position - f.Tr.position) > RangeOf(f))
                    {
                        if (f.IkDriven) ReleaseAim(f);
                        Drive(f, MainIdle, AddNone, PoseStand, now, true);
                        FaceDir(f, s.Front);
                        return;
                    }
                    f.Target = s.Threat;
                }
                Drive(f, MainIdle, AddAim, PoseStand, now, true);
                Face(f);
                if (!f.TargetIsPlayer) Aim(f, now);
                f.Target = keep;
                return;
            }
            if (f.IkDriven) ReleaseAim(f);
            Drive(f, MainIdle, AddNone, PoseStand, now, true);
            if (s != null) FaceDir(f, s.Front);
        }

        /// <summary>Stand and fire, the way every vanilla NPC does: MainState
        /// Idle, AdditionalState Shooting, held, a round every
        /// _shootingTimerDelayCached. The feet come first: a man who is not
        /// planted yet is put into Idle + Aiming, and Shooting is only asked
        /// for once that clip has faded in (Planted).</summary>
        static void Fire(Fighter f, float now)
        {
            f.Stance = Stance.Fire;
            f.HasOrder = false;
            if (f.TargetIsPlayer)
            {
                // NPC_AI2.ShootingActions fires at _killTarget by itself in this
                // state, with the vanilla hit calculation, and LookAtIkController
                // aims. Every fourth round it checks CanTouchTarget and drops to
                // Empty when it cannot: then the man is blind and runs on.
                if (f.WantAdd == AddFire && IntField(f.Ai, _fAddState, -1) == AddNone
                    && now >= f.NextState)
                {
                    f.WantAdd = AddNone;
                    f.BlindUntil = now + 2f;
                    return;
                }
                f.IkDriven = false;
                Drive(f, MainIdle, Planted(f, now) ? AddFire : AddAim, PoseStand, now, true);
                Face(f);
                return;
            }
            if (!Planted(f, now))
            {
                Drive(f, MainIdle, AddAim, PoseStand, now, true);
                Aim(f, now);
                return;
            }
            Drive(f, MainIdle, AddFire, PoseStand, now, true);
            Aim(f, now);
            if (now < f.ReactUntil || !f.IkDriven || f.AimWeight < 0.6f || now < f.NextShot) return;
            if (Shoot(f)) f.NextShot = now + ShotDelay(f);
        }

        /// <summary>Are his feet planted in a standing clip? The shooting clips
        /// are upper-body layers - NPC_AI2.SetBlendingAnimLayers puts
        /// asr_shoot_auto, rifle_shoot_samopal and hg_shoot_auto on layer 6
        /// with a spine mixing transform - and SwitchAnimationByStates plays
        /// them with Animation.CrossFade, which fades out only that layer.
        /// Straight out of a run the legs kept the run clip while
        /// IdleStateAction stopped the NavMeshAgent: the 6.16.5 men ran on the
        /// spot and fired (CONFIRMED IL). idle_aiming is a whole-body clip, so
        /// a man counts as planted once he has stood in Idle + Aiming or
        /// Shooting for the crossfade. A reload is a layer-6 clip too and
        /// neither plants nor unplants him.</summary>
        static bool Planted(Fighter f, float now)
        {
            if (IntField(f.Ai, _fMainState, -1) != MainIdle
                || IntField(f.Ai, _fPoseState, -1) != PoseStand)
            { f.PlantedSince = 0f; return false; }
            int add = IntField(f.Ai, _fAddState, -1);
            if (add == AddReload) return false;
            if (add != AddAim && add != AddFire) { f.PlantedSince = 0f; return false; }
            if (f.PlantedSince <= 0f) f.PlantedSince = now;
            return now - f.PlantedSince >= PlantSeconds;
        }

        /// <summary>His target has just gone out of sight: feet stay planted in
        /// the standing aim clip and the weapon stays on the spot.</summary>
        static void Steady(Fighter f, float now)
        {
            f.Stance = Stance.Hold;
            f.HasOrder = false;
            Drive(f, MainIdle, AddAim, PoseStand, now, true);
            Face(f);
            if (f.TargetIsPlayer) f.IkDriven = false;
            else Aim(f, now);
        }

        /// <summary>The NPC's own pause between two rounds:
        /// NPC_AI2.Start draws _shootingTimerDelayCached from 0.2..0.3 s and
        /// ShootToTarget waits exactly that long (Crew shortens it for an MG42
        /// crewman). FireTo additionally keeps the weapon's rate of fire.</summary>
        static float ShotDelay(Fighter f)
        {
            if (f.Squad != null && f.Class == SquadClass.Sniper)
            {
                float slow = CfgSniperShotSeconds == null ? 4f : CfgSniperShotSeconds.Value;
                return Mathf.Clamp(slow, 0.5f, 10f) * UnityEngine.Random.Range(0.85f, 1.15f);
            }
            if (_fShotDelayCached != null && f.Ai != null)
            {
                try
                {
                    float cached = (float)_fShotDelayCached.GetValue(f.Ai);
                    if (cached > 0.01f && cached < 2f) return cached;
                }
                catch { }
            }
            return UnityEngine.Random.Range(0.2f, 0.3f);
        }

        /// <summary>Suppression wears off, and a defender notices how badly he
        /// is hurt about twice a second.</summary>
        static void Decay(Fighter f, float now)
        {
            if (CfgSuppression.Value)
                f.Suppression = Mathf.Max(0f, f.Suppression - Time.deltaTime * 0.28f);
            else f.Suppression = 0f;
            if (now < f.NextHurt) return;
            f.NextHurt = now + 0.5f;
            f.Hurt = 1f - Mathf.Clamp01(HealthFraction(f));
        }

        /// <summary>Run to a spot and forget the old one.</summary>
        static void Send(Fighter f, Vector3 spot, float now)
        {
            f.InCover = false;
            f.Cover = Vector3.zero;
            Go(f, spot, MainRun, PoseStand, now, Stance.Reposition);
        }

        /// <summary>He is where he was sent. If that was cover, he is in it.</summary>
        static void Arrived(Fighter f)
        {
            f.HasOrder = false;
            f.InCover = f.Cover != Vector3.zero && Flat(f.Tr.position - f.Cover) < 8f;
        }

        /// <summary>A defender has no arrow and no line; he fights where he was
        /// attacked and may go to ground for it.</summary>
        static void RunDefender(Fighter d, float now)
        {
            EnsureArmed(d, now);
            Decay(d, now);
            Acquire(d, now);
            Planted(d, now);
            if (d.Target == null || now - d.LastSeen > 8f)
            {
                if (d.WantAdd == AddAim || d.WantAdd == AddFire) StandDown(d);
                d.InCover = false;
                d.Cover = Vector3.zero;
                return;
            }
            if (d.Stance == Stance.Reposition && d.HasOrder)
            {
                if (now < d.MoveDeadline && Flat(d.Tr.position - d.Ordered) > 6f)
                {
                    Drive(d, MainRun, AddNone, PoseStand, now, false);
                    return;
                }
                Arrived(d);
            }
            if (!d.InCover && now >= d.NextCover && CfgCoverChance.Value > 0f)
            {
                float appetite = Mathf.Clamp01(CfgCoverChance.Value)
                               * (0.35f + 0.8f * d.Suppression + 0.8f * d.Hurt);
                d.NextCover = now + UnityEngine.Random.Range(4f, 10f) * d.Pace;
                Vector3 spot;
                if (UnityEngine.Random.value < appetite && Search()
                    && FindCover(d, d.Target.position, out spot))
                {
                    Send(d, spot, now);
                    d.Cover = spot;
                    return;
                }
            }
            if (Reloading(d))
            {
                if (d.IkDriven) ReleaseAim(d);
                Face(d);
                return;
            }
            if (!d.Armed || !d.Sees)
            {
                Hold(d, null, now);
                Face(d);
                return;
            }
            Fire(d, now);
        }

        // ------------------------------------------------------- the weapon

        static bool ReadArmed(Fighter f)
        {
            try
            {
                if (f.Ai == null || !f.Ai.gameObject.activeInHierarchy) return false;
                if (f.Wm == null && _fWeaponsManager != null)
                    f.Wm = _fWeaponsManager.GetValue(f.Ai) as Component;
                if (IntField(f.Wm, _fWeaponCategory, 0) == 0) return false;
                Component weapon = WeaponOf(f);
                return weapon != null && _mCantWork != null
                    && !(bool)_mCantWork.Invoke(weapon, null);
            }
            catch { return false; }
        }

        /// <summary>Draw through the game's own setter, then wait for its
        /// coroutine. NPC_WeaponsManager.ShowWeapon does nothing while slot 0 is
        /// already claimed, and NetworkShowWeapon claims the slot before its
        /// delay and creates the model after it. A claimed slot without a model
        /// for five seconds - the coroutine died with a disabled object - is
        /// cleared through the native hide so the next draw can start. No
        /// permanent give-up.</summary>
        static void EnsureArmed(Fighter f, float now)
        {
            bool wasArmed = f.Armed;
            f.Armed = ReadArmed(f);
            if (f.Armed)
            {
                if (!wasArmed)
                {
                    f.NextState = 0f;
                    if (CfgDebug.Value)
                        RevivalPlugin.L.LogInfo("NpcWar: weapon ready on " + f.Ai.name
                            + " item " + IntField(f.Wm, _fWeaponItem, -1));
                }
                f.EquipTries = 0;
                f.NextEquip = 0f;
                f.SlotStuckSince = 0f;
                f.EquipWarned = false;
                return;
            }
            if (wasArmed || f.IkDriven) ReleaseAim(f);
            if (f.Ai == null || !Alive(f.Ai) || !f.Ai.gameObject.activeInHierarchy
                || !IsMine(f.Ai) || Reloading(f) || now < f.NextEquip) return;
            if (_mSetMainWeaponId == null && _mEquipWeapon == null) return;
            try
            {
                if (IntField(f.Wm, _fWeaponSlot, -1) == 0)
                {
                    if (f.SlotStuckSince <= 0f) { f.SlotStuckSince = now; return; }
                    if (now - f.SlotStuckSince < 5f) return;
                    f.SlotStuckSince = 0f;
                    f.NextEquip = now + 1f;
                    if (_mEquipWeapon != null)
                        _mEquipWeapon.Invoke(f.Ai, new object[] { false, false });
                    return;
                }
                f.SlotStuckSince = 0f;
                f.NextEquip = now + (f.EquipTries >= 4 ? 10f : 3f);
                // SetMainWeaponId(id, true) is the game's complete path: it
                // fills slot 0 and starts NetworkShowWeapon. EquipWeapon alone
                // only calls ShowWeapon, which has nothing to show after the
                // Aggressive spawn left the slot at -1.
                if (_mSetMainWeaponId != null && f.WeaponId > 0)
                    _mSetMainWeaponId.Invoke(f.Ai, new object[] { f.WeaponId, true });
                else if (_mEquipWeapon != null)
                    _mEquipWeapon.Invoke(f.Ai, new object[] { true, false });
                f.EquipTries++;
                f.NextState = 0f;
            }
            catch (Exception ex)
            {
                f.EquipTries++;
                if (CfgDebug.Value && !f.EquipWarned)
                    RevivalPlugin.L.LogWarning("NpcWar: equip failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
            if (f.EquipTries >= 4 && !f.EquipWarned)
            {
                f.EquipWarned = true;
                RevivalPlugin.L.LogWarning("NpcWar: weapon not ready on " + f.Ai.name
                    + " (item " + f.WeaponId + ", slot " + IntField(f.Wm, _fWeaponSlot, -1)
                    + ", category " + IntField(f.Wm, _fWeaponCategory, 0)
                    + "); holding fire and retrying.");
            }
        }

        // ------------------------------------------------------------- sensing

        /// <summary>Pick a target, then keep the line of fire up to date. The
        /// scan about three times a second, the line of fire about as often.</summary>
        static void Acquire(Fighter f, float now)
        {
            if (now >= f.NextScan)
            {
                f.NextScan = now + 0.3f + UnityEngine.Random.value * 0.15f;
                Transform had = f.Target;
                bool checkedLos;
                if (f.Squad != null) checkedLos = PickTargetForMan(f, now);
                else checkedLos = PickTargetForDefender(f, now);
                if (f.Target != null && f.Target != had)
                {
                    // Assault troops react at once; a defender needs a moment
                    // to find out what hit him.
                    f.ReactUntil = now + (f.Squad != null
                        ? UnityEngine.Random.Range(0.05f, 0.25f)
                        : UnityEngine.Random.Range(0.3f, 0.9f)) * (1.4f - 0.4f * f.Skill);
                    f.MuzzleBlockedSince = 0f;
                    f.MateBlockedSince = 0f;
                    if (!checkedLos) { f.LastSeen = now; f.NextLos = now; }
                }
            }
            if (f.Target == null || !f.Target)
            {
                f.Target = null;
                f.Sees = false;
                f.TargetIsPlayer = false;
                return;
            }
            if (now < f.NextLos) return;
            f.NextLos = now + 0.3f + UnityEngine.Random.value * 0.15f;
            float height;
            if (IsSquadVehicle(f, f.Target)) f.Sees = VehicleVisible(f, f.Target, out height);
            else f.Sees = AimPoint(f, f.Target, out height);
            f.AimHeight = height;
            if (f.Sees) f.LastSeen = now;
        }

        static readonly Transform[] _cand = new Transform[5];
        static readonly bool[] _candPlayer = new bool[5];
        static readonly float[] _candSqr = new float[5];

        /// <summary>A squad man: keep a target he can still see; otherwise the
        /// nearest enemy he CAN see among the few nearest - a hated NPC, a man
        /// of another squad, or the player his own AI is hunting. Never his
        /// own squad, never a god-mode or safe-zone NPC. Returns whether the
        /// line of fire was checked here.</summary>
        static bool PickTargetForMan(Fighter f, float now)
        {
            float range = RangeOf(f);
            Component player = KillTarget(f);
            if (f.Target != null && f.Target && now - f.LastSeen < 0.8f)
            {
                if (f.TargetIsPlayer)
                {
                    if (player != null && player.transform == f.Target) return false;
                }
                else
                {
                    Component cur = f.Target.GetComponent(_npcType);
                    Fighter current = cur == null ? null : FighterOf(cur);
                    if (cur != null && Alive(cur)
                        && (current == null || current.Squad != f.Squad)
                        && ((current != null && current.Squad != null) || Targetable(cur))
                        && Hostile(f.Hated, FactionOf(cur))
                        && Flat(f.Target.position - f.Tr.position) <= range * 1.1f)
                        return false;
                }
            }

            int n = 0;
            Vector3 p = f.Tr.position;
            float rangeSqr = range * range;
            if (player != null)
            {
                float d = (player.transform.position - p).sqrMagnitude;
                if (d < rangeSqr) Insert(ref n, player.transform, true, d);
            }
            for (int i = 0; i < _scene.Count; i++)
            {
                Component c = _scene[i];
                if (c == null || c == f.Ai) continue;
                float d = (c.transform.position - p).sqrMagnitude;
                if (d >= rangeSqr || (n == _cand.Length && d >= _candSqr[n - 1])) continue;
                Fighter other = FighterOf(c);
                if (other != null && other.Squad == f.Squad) continue;
                if (!Hostile(f.Hated, FactionOf(c))) continue;
                if ((other == null || other.Squad == null) && !Targetable(c)) continue;
                if (!Alive(c)) continue;
                Insert(ref n, c.transform, false, d);
            }

            f.Target = null;
            f.TargetIsPlayer = false;
            f.Sees = false;
            if (n == 0)
            {
                // No infantry and no player in reach: the squad's hostile
                // vehicle, whose hull the line fires at while the anti-tank
                // gunner kills it (he picks it himself, AntiTankStep).
                // Only from about the distance the line closes to. A rifle
                // does a hull no damage at all, so a man standing off emptying
                // magazines into one 250 units away has left the assault
                // (6.17.1 field report); that far out he walks on instead.
                Squad sq = f.Squad;
                float hull = Mathf.Min(range, VehicleStandoff() * 1.5f);
                if (sq != null && sq.GroundGroup && CurrentItem(f) == LawId)
                    hull = Mathf.Min(range, CfgAntiTankLawRange == null ? 90f : CfgAntiTankLawRange.Value);
                if (sq != null && sq.Vehicle != null
                    && (f.Class != SquadClass.AntiTank || sq.GroundGroup)
                    && (sq.Vehicle.transform.position - p).sqrMagnitude < hull * hull)
                {
                    float height;
                    f.Target = sq.Vehicle.transform;
                    f.Sees = VehicleVisible(f, f.Target, out height);
                    f.AimHeight = height;
                    if (f.Sees) f.LastSeen = now;
                    f.NextLos = now + 0.3f + UnityEngine.Random.value * 0.15f;
                }
                return true;
            }
            // The nearest three are tried for a line of fire; the nearest of all
            // stays the target when none of them can be seen, so the man at
            // least turns toward it.
            int tries = Mathf.Min(n, 3);
            for (int i = 0; i < tries; i++)
            {
                float height;
                if (!AimPoint(f, _cand[i], out height)) continue;
                f.Target = _cand[i];
                f.TargetIsPlayer = _candPlayer[i];
                f.Sees = true;
                f.AimHeight = height;
                f.LastSeen = now;
                break;
            }
            if (f.Target == null)
            {
                f.Target = _cand[0];
                f.TargetIsPlayer = _candPlayer[0];
            }
            f.NextLos = now + 0.3f + UnityEngine.Random.value * 0.15f;
            if (f.Sees && !f.TargetIsPlayer)
            {
                Component ai = f.Target.GetComponent(_npcType);
                if (ai != null && FighterOf(ai) == null) Enlist(ai);
            }
            return true;
        }

        /// <summary>Keep the candidate arrays sorted by distance.</summary>
        static void Insert(ref int n, Transform t, bool player, float sqr)
        {
            int at = n < _cand.Length ? n : _cand.Length - 1;
            if (n == _cand.Length && sqr >= _candSqr[at]) return;
            while (at > 0 && _candSqr[at - 1] > sqr)
            {
                _cand[at] = _cand[at - 1];
                _candPlayer[at] = _candPlayer[at - 1];
                _candSqr[at] = _candSqr[at - 1];
                at--;
            }
            _cand[at] = t;
            _candPlayer[at] = player;
            _candSqr[at] = sqr;
            if (n < _cand.Length) n++;
        }

        /// <summary>A defender fires back at a squad man - but not always at the
        /// nearest one. Three men emptying their magazines into whoever is in
        /// front is what wiped a squad in seconds; real fire is distributed.</summary>
        static readonly Fighter[] _pick = new Fighter[3];
        static readonly float[] _pickScore = new float[3];

        /// <summary>A defender: keep a squad man he can see or saw a moment
        /// ago; otherwise the first of the three best-scored squad men he CAN
        /// see, like PickTargetForMan. Until 6.16.7 there was no line-of-fire
        /// test here and Focus counted the defender himself, so a defender
        /// swapped his target every two seconds or so, often to a man behind
        /// a wall, dropped his aim and waited out a new reaction time (E-057
        /// review). Returns whether the line of fire was checked here.</summary>
        static bool PickTargetForDefender(Fighter f, float now)
        {
            f.TargetIsPlayer = false;
            float sight = RangeOf(f);
            if (f.Target != null && f.Target)
            {
                Component cur = f.Target.GetComponent(_npcType);
                if (cur != null && Alive(cur)
                    && OtherFaction(f.Faction, FactionOf(cur))
                    && Vector3.Distance(f.Tr.position, f.Target.position) <= sight * 1.2f
                    && (f.Sees || now - f.LastSeen < 0.8f))
                    return false;
            }
            int n = 0;
            for (int q = 0; q < _squads.Count; q++)
                for (int i = 0; i < _squads[q].Men.Count; i++)
                {
                    Fighter m = _squads[q].Men[i];
                    if (m.Ai == null || m.Tr == null || !Alive(m.Ai)) continue;
                    if (!OtherFaction(f.Faction, FactionOf(m.Ai))) continue;
                    float d = Vector3.Distance(m.Tr.position, f.Tr.position);
                    if (d > sight) continue;
                    // Every OTHER defender already shooting at him counts as
                    // half the sight range of extra distance.
                    float score = d + Focus(m, f) * sight * 0.5f
                                + UnityEngine.Random.value * sight * 0.15f;
                    if (n == _pick.Length && score >= _pickScore[n - 1]) continue;
                    int k = n < _pick.Length ? n++ : n - 1;
                    while (k > 0 && _pickScore[k - 1] > score)
                    {
                        _pick[k] = _pick[k - 1];
                        _pickScore[k] = _pickScore[k - 1];
                        k--;
                    }
                    _pick[k] = m;
                    _pickScore[k] = score;
                }
            if (n == 0)
            {
                f.Target = null;
                f.Sees = false;
                return false;
            }
            Transform chosen = null;
            bool seen = false;
            for (int i = 0; i < n && chosen == null; i++)
            {
                float height;
                if (!AimPoint(f, _pick[i].Tr, out height)) continue;
                chosen = _pick[i].Tr;
                seen = true;
                f.Sees = true;
                f.AimHeight = height;
                f.LastSeen = now;
                f.NextLos = now + 0.3f + UnityEngine.Random.value * 0.15f;
            }
            // Nobody visible: the best-scored man, so he at least turns toward
            // him; Acquire then treats him as a fresh target.
            if (chosen == null) chosen = _pick[0].Tr;
            f.Target = chosen;
            for (int i = 0; i < _pick.Length; i++) _pick[i] = null;
            return seen;
        }

        static int Focus(Fighter man, Fighter asker)
        {
            int n = 0;
            for (int i = 0; i < _defenders.Count; i++)
                if (_defenders[i] != asker && _defenders[i].Target == man.Tr) n++;
            return n;
        }

        static Fighter NearestSquadMan(Fighter f, float rangeFactor)
        {
            Fighter best = null;
            float bestSqr = Mathf.Max(CfgSightRange.Value, AssaultRange()) * rangeFactor;
            bestSqr *= bestSqr;
            for (int q = 0; q < _squads.Count; q++)
                for (int i = 0; i < _squads[q].Men.Count; i++)
                {
                    Fighter m = _squads[q].Men[i];
                    if (m.Ai == null || m.Tr == null || !Alive(m.Ai)) continue;
                    if (!OtherFaction(f.Faction, FactionOf(m.Ai))) continue;
                    float d = (m.Tr.position - f.Tr.position).sqrMagnitude;
                    if (d < bestSqr) { best = m; bestSqr = d; }
                }
            return best;
        }

        /// <summary>The attacked NPC fights back - and so do comrades of its
        /// faction standing close to it, which is what a real ambush on a camp
        /// looks like. Alarmed through the vanilla call so it also engages a
        /// hostile player the normal way. They do not all react at once.</summary>
        static void Enlist(Component struck)
        {
            int cap = CfgMaxCombatants.Value;
            // The NPC actually shot at always gets a slot, one over the cap if
            // need be. Before 6.16.7 the scene walk ran in scene order and could
            // fill the cap with bystanders before it reached him.
            if (FighterOf(struck) == null && _defenders.Count < cap + 1
                && Alive(struck) && Targetable(struck))
                AddDefender(struck, 0f);
            object faction = FactionOf(struck);
            if (faction == null) return;
            Vector3 at = struck.transform.position;
            for (int i = 0; i < _scene.Count && _defenders.Count < cap; i++)
            {
                Component c = _scene[i];
                if (c == null || c == struck) continue;
                // Cheapest test first: the rest is reflection.
                float away = (c.transform.position - at).magnitude;
                if (away > EnlistRadius) continue;
                object other = FactionOf(c);
                if (other == null || !other.Equals(faction)) continue;
                if (FighterOf(c) != null || !Alive(c) || !Targetable(c)) continue;
                AddDefender(c, away);
            }
        }

        static void AddDefender(Component c, float away)
        {
            Fighter d = NewFighter(c, null);
            d.ReactUntil = Time.time + UnityEngine.Random.Range(0.4f, 1.5f) + away * 0.03f;
            d.NextScan = Time.time;
            _defenders.Add(d);
            TryAlarm(c);
        }

        // ------------------------------------------------------------- firing

        /// <summary>One round at the man's NPC target. False when the weapon
        /// did not fire this frame (rate of fire, reload, obstruction), so the
        /// next attempt comes quickly.</summary>
        static bool Shoot(Fighter f)
        {
            if (!ReadArmed(f)) return false;
            Vector3 aimAt = AimWorld(f);
            Component weapon = WeaponOf(f);
            Vector3 from = Muzzle(weapon, f);
            if (from == Vector3.zero) return false;
            // Eyes can see over cover while the barrel is still behind it.
            bool hull = IsSquadVehicle(f, f.Target);
            if (hull ? !VehicleClear(from, aimAt, f.Squad.Vehicle) : !Clear(from, aimAt, f.Target))
            {
                if (f.MuzzleBlockedSince <= 0f) f.MuzzleBlockedSince = Time.time;
                f.NextShot = Time.time + 0.2f;
                return false;
            }
            f.MuzzleBlockedSince = 0f;
            // Never through a comrade of the line.
            if (f.Squad != null && MateInLine(f, from, aimAt))
            {
                if (f.MateBlockedSince <= 0f) f.MateBlockedSince = Time.time;
                f.NextShot = Time.time + 0.15f;
                return false;
            }
            f.MateBlockedSince = 0f;
            float dist = Vector3.Distance(from, aimAt);
            Component targetAi = f.Target.GetComponent(_npcType);
            Fighter victim = targetAi == null ? null : FighterOf(targetAi);
            Vector3 aim = aimAt + MissOffset(f, victim, from, aimAt, dist);
            bool rocket = IntField(f.Wm, _fWeaponItem, -1) == Crew.LAW_ID;
            // CrewLaw already supplies the LAW blast and NPC damage. Keep it
            // away from the shooter and nearby squad mates, and do not add a
            // second infantry hit after its FireOneShot postfix.
            if (rocket && !RocketClear(f, from, aim))
            {
                f.NextShot = Time.time + 0.5f;
                return false;
            }

            int fired = VanillaShot(f, weapon, aim);
            if (fired <= 0)
            {
                f.NextShot = Time.time + 0.05f;
                return false;
            }
            if (f.Squad != null) f.Squad.Shots++;
            else if (victim != null && victim.Squad != null) victim.Squad.TakenShots++;
            if (rocket)
            {
                // The anti-tank gunner's LAW rounds are counted: AntiTankRockets
                // in all, then the rifle again (AntiTankStep).
                if (f.Squad != null && f.Class == SquadClass.AntiTank && f.LawLeft > 0)
                {
                    f.LawLeft--;
                    f.Squad.Rockets++;
                    RevivalPlugin.L.LogInfo("NpcWar: " + f.Squad.Tag + " anti-tank gunner fired a LAW at "
                        + f.Target.name + " (" + dist.ToString("0") + " units), " + f.LawLeft + " left.");
                }
                return true;
            }
            // Being shot at is felt whether or not the round connects.
            if (victim != null && CfgSuppression.Value)
                victim.Suppression = Mathf.Min(1f,
                    victim.Suppression + 0.34f / Mathf.Max(0.4f, victim.Nerve));

            Vector3 dir = aim - from;
            if (dir.sqrMagnitude < 0.0001f) return true;
            dir.Normalize();

            float range = Mathf.Max(dist + 5f, RangeOf(f) + 20f);
            Vector3 impact;
            // Past the shooter's own 0.75 unit capsule.
            GameObject struck = Turret.RaycastObject(from + dir * 1.0f, dir, range, out impact);

            if (struck == null) return true;
            Component hitAi = struck.GetComponentInParent(_npcType);
            if (hitAi == null || !Alive(hitAi)) return true;
            Fighter hurt = FighterOf(hitAi);
            if (hurt != null && hurt.Squad != null && hurt.Squad == f.Squad) return true;
            bool enemy = OtherFaction(f.Faction, FactionOf(hitAi)) && (f.Squad == null
                ? hurt != null && hurt.Squad != null
                : Hostile(f.Hated, FactionOf(hitAi))
                  && ((hurt != null && hurt.Squad != null) || Targetable(hitAi)));
            if (!enemy) return true;
            if (hurt == null && f.Squad != null) Enlist(hitAi);

            float damage = CfgDamage.Value;
            if (f.Squad == null && hurt != null && hurt.Squad != null)
                damage = DefenderRound(hurt, damage);
            else if (f.Squad != null && f.Class == SquadClass.Sniper)
                damage *= SniperDamage();
            BreakKillStreak(hitAi);
            try
            {
                if (Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", damage))
                {
                    if (f.Squad != null) f.Squad.Hits++;
                    else if (hurt != null && hurt.Squad != null) hurt.Squad.TakenHits++;
                    if (CfgDebug.Value)
                        RevivalPlugin.L.LogInfo("NpcWar: hit at " + dist.ToString("0") + " units.");
                }
            }
            catch (Exception ex)
            {
                // Safety net only; BreakKillStreak removes the known cause. A
                // throw that leaves the NPC dead still counts as a hit. The
                // first one per operation is logged whatever Debug says: the
                // 6.16.6 stack trace was the only way to find the vanish.
                bool dead = !Alive(hitAi);
                Squad owner = f.Squad != null ? f.Squad : (hurt != null ? hurt.Squad : null);
                if (dead && f.Squad != null) f.Squad.Hits++;
                else if (dead && owner != null) owner.TakenHits++;
                if (owner == null || !owner.DamageErrorLogged)
                {
                    if (owner != null) owner.DamageErrorLogged = true;
                    RevivalPlugin.L.LogWarning("NpcWar: damage call threw (target "
                        + (dead ? "dead" : "alive") + ") - " + ex);
                }
            }
            return true;
        }

        /// <summary>Would this round pass within a man's width of a living
        /// comrade standing between the muzzle and the target?</summary>
        static bool MateInLine(Fighter f, Vector3 from, Vector3 to)
        {
            Vector3 axis = to - from;
            float length = axis.magnitude;
            if (length < 4f) return false;
            axis /= length;
            for (int i = 0; i < f.Squad.Men.Count; i++)
            {
                Fighter m = f.Squad.Men[i];
                if (m == f || m.Tr == null || m.Ai == null || !Alive(m.Ai)) continue;
                Vector3 chest = m.Tr.position + Vector3.up * ChestHeight - from;
                float t = Vector3.Dot(chest, axis);
                if (t < 2f || t > length - 2f) continue;
                if ((chest - axis * t).sqrMagnitude < 2.0f * 2.0f) return true;
            }
            return false;
        }

        static bool RocketClear(Fighter f, Vector3 from, Vector3 aim)
        {
            float radius = RevivalPlugin.CfgPatrolCrewLawRadius == null
                ? 8f : Mathf.Max(0f, RevivalPlugin.CfgPatrolCrewLawRadius.Value);
            Vector3 dir = aim - from;
            float dist = dir.magnitude;
            if (dist < radius * 2f + 10f) return false;
            Vector3 hit;
            GameObject obstacle = Turret.RaycastObject(from, dir / dist, dist + 1f, out hit);
            Vector3 boom = obstacle == null ? aim : hit;
            if (Vector3.Distance(from, boom) < radius * 2f + 10f) return false;
            if (f.Squad != null)
                foreach (Fighter mate in f.Squad.Men)
                    if (mate.Tr != null && Alive(mate.Ai)
                        && Vector3.Distance(mate.Tr.position, boom) < radius + 6f) return false;
            return true;
        }

        /// <summary>Fire the NPC's own weapon at a point. 1 = a round went out,
        /// 0 = not this frame (rate of fire, empty and reloading), -1 = no usable
        /// native weapon. Failure never produces effects or damage.</summary>
        static int VanillaShot(Fighter f, Component weapon, Vector3 aim)
        {
            if (weapon == null || _mFireTo == null || _mHasBullets == null || _fRofDelay == null)
                return -1;
            try
            {
                if (_mCantWork != null && (bool)_mCantWork.Invoke(weapon, null)) return -1;
                // FireTo advances its delay even with an empty magazine, so
                // ammunition must be checked BEFORE using the delay as proof.
                if (!(bool)_mHasBullets.Invoke(weapon, null))
                {
                    StartReload(f);
                    return 0;
                }
                float before = (float)_fRofDelay.GetValue(weapon);
                if (before >= Time.time) return 0;
                if (_fAimingPoint != null && _fAimingPoint.FieldType == typeof(Vector3))
                    _fAimingPoint.SetValue(f.Ai, aim);
                _mFireTo.Invoke(weapon, new object[] { aim, true });
                float after = (float)_fRofDelay.GetValue(weapon);
                return after != before ? 1 : 0;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: native shot failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return -1;
            }
        }

        /// <summary>An empty magazine: the game's own reload, exactly what
        /// ShootingActions does on the master (reload state, animation, RPC).</summary>
        static void StartReload(Fighter f)
        {
            if (_mBulletsEnded == null || Reloading(f)) return;
            ReleaseAim(f);
            try
            {
                _mBulletsEnded.Invoke(f.Ai, null);
                f.WantAdd = -1;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: reload failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        /// <summary>Legacy event receiver for an older peer's shot packet.
        /// New NPC shots use only native weapon effects and never call this.</summary>
        internal static void ShotEffect(Vector3 from, Vector3 end, bool sound)
        {
            try
            {
                if (sound) VehicleShotSound.Play(from, false);
                List<Vector3> path = new List<Vector3>();
                path.Add(from + (end - from).normalized * 1.2f);
                path.Add(end);
                RocketHook.SpawnTracer(path, 0.06f, 0.03f,
                    new Color(1f, 0.9f, 0.6f, 1f), new Color(1f, 0.6f, 0.2f, 0.6f), 0.08f);
            }
            catch (Exception ex)
            {
                if (CfgDebug != null && CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: shot effect - " + ex.Message);
            }
        }

        /// <summary>Where the round actually goes. Marksmanship, suppression,
        /// range, and what the man being shot at is doing about it.</summary>
        static Vector3 MissOffset(Fighter shooter, Fighter victim, Vector3 from,
                                  Vector3 to, float dist)
        {
            // 6.16.7: the squad's eight-point allowance is gone and its fire
            // falls off harder over its long AssaultRange; 15 men with a bonus
            // wiped a settlement for 3 losses (E-057). 6.19 takes the steeper
            // half of that back (0.55 -> 0.4): the squad and the defenders now
            // lose the same share of their hit chance over the same reach, and
            // nobody is punished for the assault range being the longer one.
            bool marksman = shooter.Squad != null && shooter.Class == SquadClass.Sniper;
            float acc = Mathf.Clamp01(CfgAccuracy.Value) * shooter.Skill;
            // 6.19: a marksman is picked for his shooting. His personal draw
            // still separates a good sniper from a very good one, but only
            // SniperSkill of it, and what shakes a rifleman - being shot at, a
            // target behind cover, a target that moves - costs him SniperSteady
            // of the same penalty. He pays for it with SniperShotSeconds.
            if (marksman)
                acc = SniperAccuracy() * (1f - SniperSkill + SniperSkill * shooter.Skill);
            acc *= 1f - (marksman ? 0.45f * SniperSteady : 0.45f) * shooter.Suppression;
            if (victim != null)
            {
                if (victim.InCover) acc *= Steadied(0.55f, marksman);
                else if (victim.Crouched) acc *= Steadied(0.75f, marksman);
                if (victim.Stance == Stance.Reposition || victim.Stance == Stance.Bound
                    || victim.Stance == Stance.Advance) acc *= Steadied(0.8f, marksman);
            }
            float far = Mathf.Clamp01(dist / Mathf.Max(1f, RangeOf(shooter)));
            float falloff = shooter.Squad == null ? 0.4f
                : (marksman ? SniperFalloff : SquadFalloff);
            if (UnityEngine.Random.value <= acc * (1f - falloff * far)) return Vector3.zero;

            Vector3 axis = (to - from).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, axis);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.right;
            side.Normalize();
            Vector3 up = Vector3.Cross(axis, side).normalized;
            float ang = UnityEngine.Random.value * Mathf.PI * 2f;
            float amt = Mathf.Max(0.4f, CfgSpread.Value) * (0.5f + UnityEngine.Random.value);
            return (side * Mathf.Cos(ang) + up * Mathf.Sin(ang)) * amt;
        }

        /// <summary>One of the factors a target's cover, crouch or movement
        /// takes off a shooter's hit chance - and what is left of that penalty
        /// for a marksman, who takes the time to shoot round it.</summary>
        static float Steadied(float penalty, bool marksman)
        {
            return marksman ? 1f - (1f - penalty) * SniperSteady : penalty;
        }

        // ------------------------------------------------------------- aiming

        /// <summary>The highest part of the target this man actually has a line
        /// to: chest first, then the head, then the waist. A rifleman on a tower
        /// behind a parapet only ever shows his head and chest. Returns false
        /// when nothing is visible; the aim point is then the chest, for the
        /// pose. The HEIGHT comes back, not the point, so the weapon keeps
        /// following a target that is moving between two line-of-fire checks.</summary>
        static bool AimPoint(Fighter f, Transform target, out float height)
        {
            Vector3 eye = f.Tr.position + Vector3.up * (f.Crouched ? CrouchEye : EyeHeight);
            height = ChestHeight;
            if (Clear(eye, target.position + Vector3.up * ChestHeight, target)) return true;
            if (Clear(eye, target.position + Vector3.up * HeadHeight, target))
            { height = HeadHeight; return true; }
            if (Clear(eye, target.position + Vector3.up * WaistHeight, target))
            { height = WaistHeight; return true; }
            return false;
        }

        /// <summary>The world point this man is holding his weapon on.</summary>
        static Vector3 AimWorld(Fighter f)
        {
            return f.Target.position + Vector3.up * f.AimHeight;
        }

        /// <summary>Is the way from a to b open? A hit on the target itself
        /// counts as open, and so does one within 1.2 units of b - a shade more
        /// than a man's own 0.75 unit capsule, for a collider that does not hang
        /// under his transform. NOT more: 6.16.1 allowed two and a half units,
        /// and a railing struck 0.9 m from a man's head passed as a line of
        /// fire, so the squad emptied its magazines into the railing.</summary>
        static bool Clear(Vector3 from, Vector3 to, Transform target)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 1f) return true;
            dir /= dist;
            Vector3 point;
            // Start past the shooter's own 0.75 unit capsule and ragdoll.
            GameObject hit = Turret.RaycastObject(from + dir * 1.2f, dir, dist, out point);
            if (hit == null) return true;
            if (target != null && hit.transform.IsChildOf(target)) return true;
            return (to - point).sqrMagnitude < 1.44f;
        }

        /// <summary>Turn the body toward the target and point the WEAPON at it.
        /// The second half is the part the game cannot do for us: NPC_AI2's
        /// LookAtIkController only runs the aim IK while _killTarget is set,
        /// and _killTarget is a player.</summary>
        static void Aim(Fighter f, float now)
        {
            if (f.Target == null || !f.Target) return;
            Face(f);
            Vector3 look = AimWorld(f);
            // A little wander, so a line of men does not hold one statue pose.
            // Roughly a hand's width at fifty units.
            float t = now * (0.7f + (f.LaneOffset * 0.013f));
            look += new Vector3(Mathf.Sin(t * 1.3f), Mathf.Sin(t * 0.9f + 1.1f),
                                Mathf.Cos(t * 1.1f)) * (0.3f + 0.5f * f.Suppression);
            DriveAim(f, look, now);
            if (_fAimingPoint != null && _fAimingPoint.FieldType == typeof(Vector3))
            {
                try { _fAimingPoint.SetValue(f.Ai, look); }
                catch { }
            }
        }

        /// <summary>Turn the body toward the target. Each man turns at his own
        /// speed, so a line that acquires together does not snap together.</summary>
        static void Face(Fighter f)
        {
            if (f.Target == null || !f.Target) return;
            Vector3 flat = f.Target.position - f.Tr.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) return;
            f.Tr.rotation = Quaternion.RotateTowards(f.Tr.rotation,
                Quaternion.LookRotation(flat), (180f + 120f * f.Skill) * Time.deltaTime);
        }

        /// <summary>Turn the body onto a direction - the line's front, when
        /// there is nothing in reach to turn onto. Same rate as Face.</summary>
        static void FaceDir(Fighter f, Vector3 dir)
        {
            if (f.Tr == null) return;
            Vector3 flat = FlatV(dir);
            if (flat.sqrMagnitude < 0.01f) return;
            f.Tr.rotation = Quaternion.RotateTowards(f.Tr.rotation,
                Quaternion.LookRotation(flat), (180f + 120f * f.Skill) * Time.deltaTime);
        }

        /// <summary>Hold the aim IK on a world point. SetupLookAtIk wires
        /// LookAtIKTarget as the solver target with a chest-to-hand chain, so
        /// moving that transform points the weapon - in elevation as well as in
        /// azimuth. The vanilla controller lerps the weight toward zero every
        /// frame while _killTarget is null; we ramp our own weight like its
        /// player branch does (5 per second) and set it every frame.</summary>
        static void DriveAim(Fighter f, Vector3 lookAt, float now)
        {
            if (!AimPoseReady(f, now)) { if (f.IkDriven) ReleaseAim(f); return; }
            if (_fAimIk == null || _fLookTarget == null) return;
            if (f.IkMissing)
            {
                // Unity can finish wiring _aimIk one or two frames after the
                // NPC starts. A permanent failure bit made that race fatal.
                if (now < f.NextIkRetry) return;
                f.IkMissing = false;
                f.Ik = null;
                f.Look = null;
            }
            try
            {
                if (f.Ik == null)
                {
                    f.Ik = _fAimIk.GetValue(f.Ai) as Component;
                    f.Look = _fLookTarget.GetValue(f.Ai) as Transform;
                    if (f.Ik == null || f.Look == null)
                    { f.IkMissing = true; f.NextIkRetry = now + 1f; return; }
                    if (_fSolver == null) _fSolver = AccessTools.Field(f.Ik.GetType(), "solver");
                    if (_fSolver == null)
                    { f.IkMissing = true; f.NextIkRetry = now + 1f; return; }
                }
                object solver = _fSolver.GetValue(f.Ik);
                if (solver == null)
                { f.IkMissing = true; f.NextIkRetry = now + 1f; return; }
                if (_fIkWeight == null)
                {
                    _fIkWeight = AccessTools.Field(solver.GetType(), "IKPositionWeight");
                    if (_fIkWeight == null || _fIkWeight.FieldType != typeof(float))
                    { f.IkMissing = true; f.NextIkRetry = now + 1f; return; }
                }
                GameObject go = f.Ik.gameObject;
                if (!go.activeSelf) go.SetActive(true);
                f.AimWeight = Mathf.Min(1f, f.AimWeight + Time.deltaTime * 5f);
                _fIkWeight.SetValue(solver, f.AimWeight);
                // Snap on the first frame of an engagement, then follow.
                f.Look.position = f.IkDriven
                    ? Vector3.Lerp(f.Look.position, lookAt, Time.deltaTime * 8f)
                    : lookAt;
                f.IkDriven = true;
            }
            catch (Exception ex)
            {
                ReleaseAim(f);
                f.IkMissing = true;
                f.NextIkRetry = now + 1f;
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: aim IK - " + ex.Message);
            }
        }

        /// <summary>The same gate the vanilla LookAtIkController has - the man
        /// stands in Aiming or Shooting - plus the two things that made
        /// 6.16.2's men arch their backs: a weapon really in hand, and a short
        /// moment for the crossfade out of the run or draw clip.</summary>
        static bool AimPoseReady(Fighter f, float now)
        {
            if (!f.Armed || Reloading(f)
                || IntField(f.Ai, _fMainState, -1) != MainIdle
                || IntField(f.Ai, _fPoseState, -1) != PoseStand)
            { f.PoseSince = 0f; return false; }
            int add = IntField(f.Ai, _fAddState, -1);
            if (add != AddAim && add != AddFire) { f.PoseSince = 0f; return false; }
            if (f.PoseSince <= 0f) { f.PoseSince = now; return false; }
            return now - f.PoseSince >= 0.15f;
        }

        static void ReleaseAim(Fighter f)
        {
            f.IkDriven = false;
            f.AimWeight = 0f;
            // A lowered hand must not inherit a second of vanilla IK fade.
            try
            {
                if (f.Ik == null || _fSolver == null || _fIkWeight == null) return;
                object solver = _fSolver.GetValue(f.Ik);
                if (solver != null) _fIkWeight.SetValue(solver, 0f);
            }
            catch { }
        }

        /// <summary>Out of the aim pose. The vanilla controller switches the IK
        /// off again on its own once the additional state is no longer Aiming
        /// or Shooting.</summary>
        static void StandDown(Fighter f)
        {
            ReleaseAim(f);
            f.InCover = false;
            int add = IntField(f.Ai, _fAddState, -1);
            if (add != AddAim && add != AddFire) { f.WantAdd = AddNone; return; }
            Drive(f, MainIdle, AddNone, IntField(f.Ai, _fPoseState, PoseStand), Time.time, false);
        }

        // ----------------------------------------------------------- movement

        /// <summary>Send one man to a world point on the NavMesh and remember
        /// what he was sent to do.</summary>
        static void Go(Fighter f, Vector3 dest, int state, int pose, float now, Stance stance)
        {
            f.Stance = stance;
            f.Crouched = pose == PoseCrouch;
            ReleaseAim(f);
            OrderMove(f, dest, state, AddNone, pose);
        }

        /// <summary>Send one man to a world point on the NavMesh: a tactical
        /// task, one tactical walk point there, and SetStateWithAnimAndSync,
        /// which drives the NavMeshAgent, the animation and the Photon sync.
        /// State 1 walks (IdleStateAction uses it for patrol points), 2 runs.</summary>
        static void OrderMove(Fighter f, Vector3 dest, int state, int add, int pose)
        {
            if (_mTempTask == null || _mTargetWp == null || _mStateSync == null
                || _fTempPoints == null || _wpTacticalValue == null) return;
            try
            {
                // Ground group destinations already passed the bounded path
                // check. A second, wider projection could leave their radius.
                Vector3 target = f.Squad != null && f.Squad.GroundGroup ? dest : Ground(dest);
                if (f.Point == null)
                {
                    f.Point = new GameObject("NpcWarPoint");
                    f.Point.transform.SetParent(_pointsRoot, false);
                    Component made = f.Point.AddComponent(_wpType);
                    _fWpType.SetValue(made, _wpTacticalValue);
                }
                f.Point.transform.position = target;
                Component wp = f.Point.GetComponent(_wpType);

                IList list = Activator.CreateInstance(_fTempPoints.FieldType) as IList;
                if (list == null) return;
                list.Add(wp);

                // NavAgentMoveToPos clears the path with isStopped = true before
                // it sets the new one (CONFIRMED IL, 6.17): a man already running
                // there would lose his speed on every order. Keep what he had.
                NavMeshAgent agent = Agent(f);
                bool moving = agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh
                    && agent.hasPath && IntField(f.Ai, _fMainState, -1) == state;
                Vector3 velocity = moving ? agent.velocity : Vector3.zero;

                _mTempTask.Invoke(f.Ai, new object[] { Arg(_mTempTask, 0, 2) });   // Tactical
                _fTempPoints.SetValue(f.Ai, list);
                if (_fTempIndex != null) _fTempIndex.SetValue(f.Ai, 0);
                _mTargetWp.Invoke(f.Ai, new object[] { wp });
                Quiet(f, false);
                if (!SetState(f, state, add, pose, 0, f.Tr.eulerAngles.y)) return;
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    if (agent.isStopped) agent.isStopped = false;
                    if (moving && velocity.sqrMagnitude > 0.01f) agent.velocity = velocity;
                }
                f.WantMain = state; f.WantAdd = add; f.WantPose = pose;
                f.NextState = Time.time + 0.3f;
                f.HasOrder = true;
                f.Ordered = dest;
                // Generous: a run of 60 units at the game's pace plus slack.
                f.MoveDeadline = Time.time + 6f + Flat(dest - f.Tr.position) * 0.25f;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: move order failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        /// <summary>Make sure the game is showing what we want it to show, and
        /// send nothing when it already is. Every SetStateWithAnimAndSync is an
        /// RPC to every player around and restarts the clip.</summary>
        static void Drive(Fighter f, int main, int add, int pose, float now, bool hold)
        {
            // The reload is the game's business from OnBulletsEnded to the end
            // of the coroutine; overwriting AdditionalState 2 cancels it.
            if (Reloading(f)) return;
            if (hold) Quiet(f, true);
            int liveMain = IntField(f.Ai, _fMainState, -1);
            int liveAdd = IntField(f.Ai, _fAddState, -1);
            int livePose = IntField(f.Ai, _fPoseState, -1);
            if (liveMain == main && liveAdd == add && livePose == pose)
            {
                f.WantMain = main; f.WantAdd = add; f.WantPose = pose;
                return;
            }
            if (now < f.NextState) return;
            f.NextState = now + 0.28f;
            float rotY = f.Tr.eulerAngles.y;
            if (f.Target != null && f.Target)
            {
                Vector3 flat = f.Target.position - f.Tr.position;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f) rotY = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            }
            if (SetState(f, main, add, pose, main == MainIdle ? -1 : 0, rotY))
            {
                f.WantMain = main; f.WantAdd = add; f.WantPose = pose;
                if (main == MainIdle && add != AddNone && _mStartRotation != null
                    && f.Target != null && f.Target)
                {
                    try { _mStartRotation.Invoke(f.Ai, new object[] { f.Target.position }); }
                    catch { }
                }
            }
        }

        /// <summary>Keep the vanilla idle logic off a man we are driving.
        /// IdleStateAction queues its own intentions on the tactical task and
        /// returns early while GetCalculatedPauseTime is positive, so a short
        /// pause refreshed twice a second is enough to hold a pose.</summary>
        static void Quiet(Fighter f, bool pause)
        {
            float now = Time.time;
            if (pause && now < f.PauseUntil) return;
            try
            {
                if (_mClearIntentions != null) _mClearIntentions.Invoke(f.Ai, null);
                if (_mPauseTime == null) return;
                if (!pause)
                {
                    // A man who has just been sent somewhere must not sit out
                    // the pause his last aim hold left behind.
                    f.PauseUntil = 0f;
                    _mPauseTime.Invoke(f.Ai, new object[] { 0f });
                    return;
                }
                f.PauseUntil = now + 0.5f;
                _mPauseTime.Invoke(f.Ai, new object[] { 1.1f });
            }
            catch { }
        }

        /// <summary>Does the man still walk to the point we gave him? The alarm's
        /// tactical intentions replace the temporary list with the settlement's
        /// tactical points (IntentionsActions IL_044F).</summary>
        static bool StillOurPoint(Fighter f)
        {
            if (f.Point == null || _fTempPoints == null) return false;
            try
            {
                IList list = _fTempPoints.GetValue(f.Ai) as IList;
                return list != null && list.Count == 1
                    && list[0] as Component == f.Point.GetComponent(_wpType);
            }
            catch { return false; }
        }

        /// <summary>NPC_AI2.SetStateWithAnimAndSync(position, main, additional,
        /// pose, walk point index, use temporary points, temporary task, rotY)
        /// with the game's own enum types built from the numbers.</summary>
        static bool SetState(Fighter f, int main, int additional, int pose, int index, float rotY)
        {
            if (_mStateSync == null) return false;
            try
            {
                bool useTemp = _fUseTemp == null || !(_fUseTemp.GetValue(f.Ai) is bool)
                    || (bool)_fUseTemp.GetValue(f.Ai);
                int task = IntField(f.Ai, _fTempTaskField, 2);
                if (main != MainIdle) { useTemp = true; task = 2; }
                _mStateSync.Invoke(f.Ai, new object[] {
                    f.Tr.position, Arg(_mStateSync, 1, main), Arg(_mStateSync, 2, additional),
                    Arg(_mStateSync, 3, pose), Arg(_mStateSync, 4, index), useTemp,
                    Arg(_mStateSync, 6, task), rotY });
                return true;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: state change failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return false;
            }
        }

        static object Arg(MethodInfo m, int index, int value)
        {
            ParameterInfo[] ps = m.GetParameters();
            if (index >= ps.Length) return value;
            Type t = ps[index].ParameterType;
            return t.IsEnum ? Enum.ToObject(t, value) : (object)value;
        }

        /// <summary>The walkable point under a destination. The local ray comes
        /// first so a floor above the terrain (a bridge, a building) wins, and
        /// the terrain HEIGHT DATA answers when the ray finds nothing at all:
        /// away from every player the whole-map TerrainColliders are off
        /// (E-059), and a point left at the arrow's own height then misses the
        /// NavMesh by the height of the hill it crosses - which is exactly how
        /// a squad stops walking as soon as nobody is watching it.</summary>
        static Vector3 Ground(Vector3 p)
        {
            Vector3 hit;
            GameObject g = Turret.RaycastObject(p + Vector3.up * 30f, Vector3.down, 80f, out hit);
            Vector3 at = g == null ? p : hit + Vector3.up * 0.1f;
            if (g == null)
            {
                float y;
                if (RevivalTroopInsertion.TerrainHeight(p, out y))
                    at = new Vector3(p.x, y + 0.1f, p.z);
            }
            NavMeshHit nav;
            if (NavMesh.SamplePosition(at, out nav, 12f, NavMesh.AllAreas)) return nav.position;
            // A wider net before giving up: a point that is off the mesh is a
            // point the man will never walk to.
            if (NavMesh.SamplePosition(at, out nav, 60f, NavMesh.AllAreas)) return nav.position;
            return at;
        }

        static void EnsurePointsRoot()
        {
            if (_pointsRoot != null) return;
            GameObject root = new GameObject("NpcWarPoints");
            UnityEngine.Object.DontDestroyOnLoad(root);
            _pointsRoot = root.transform;
        }

        static void TryAlarm(Component ai)
        {
            if (_mAlarm == null) return;
            try { _mAlarm.Invoke(ai, new object[] { true }); }
            catch (Exception ex)
            {
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogWarning("NpcWar: alarm failed - "
                        + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        // ------------------------------------------------ defender cover search

        static bool Search()
        {
            if (_searchBudget <= 0) return false;
            _searchBudget--;
            return true;
        }

        /// <summary>Somewhere close that breaks the line from the threat. Best
        /// is a spot that hides a crouching man but still lets a standing one
        /// shoot - the edge of a wall rather than the middle of it.</summary>
        static bool FindCover(Fighter f, Vector3 threat, out Vector3 spot)
        {
            spot = Vector3.zero;
            Vector3 me = f.Tr.position;
            Vector3 away = me - threat;
            away.y = 0f;
            if (away.sqrMagnitude < 1f) return false;
            float baseAngle = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg;
            Vector3 aimAtThreat = threat + Vector3.up * ChestHeight;
            float best = 0f;

            for (int i = 0; i < 8; i++)
            {
                float ang = baseAngle + UnityEngine.Random.Range(-115f, 115f);
                float rad = UnityEngine.Random.Range(7f, 28f);
                Vector3 c = me + new Vector3(Mathf.Sin(ang * Mathf.Deg2Rad), 0f,
                                             Mathf.Cos(ang * Mathf.Deg2Rad)) * rad;
                if (!Reachable(f, c, out c)) continue;
                bool hiddenLow = !Clear(c + Vector3.up * 1.9f, aimAtThreat, null);
                if (!hiddenLow) continue;              // no protection, no cover
                bool canShoot = Clear(c + Vector3.up * EyeHeight, aimAtThreat, null);
                float score = 1f + (canShoot ? 1.4f : 0f)
                            - Mathf.Abs(rad - 16f) / 40f
                            + UnityEngine.Random.value * 0.25f;
                if (score > best) { best = score; spot = c; }
            }
            return best > 0f;
        }

        /// <summary>Is a proposed point real ground the man may stand on?</summary>
        static bool Reachable(Fighter f, Vector3 c, out Vector3 at)
        {
            at = c;
            Vector3 hit;
            GameObject g = Turret.RaycastObject(new Vector3(c.x, c.y + 40f, c.z),
                Vector3.down, 120f, out hit);
            if (g == null) return false;
            if (Mathf.Abs(hit.y - f.Tr.position.y) > 26f) return false;
            NavMeshHit nav;
            if (!NavMesh.SamplePosition(new Vector3(c.x, hit.y + 0.2f, c.z), out nav, 8f,
                                        NavMesh.AllAreas))
                return false;
            at = nav.position;
            return true;
        }

        // ------------------------------------------------------------ classes

        /// <summary>The editor's class name, normalized. Anything unknown is a
        /// regular: troopdef.py validates, this is the second line.</summary>
        internal static string ClassKey(string raw)
        {
            string k = raw == null ? "" : raw.Trim().ToLowerInvariant();
            switch (k)
            {
                case "sniper":
                case "tank":
                case "defender":
                case "antitank":
                    return k;
                default:
                    return "regular";
            }
        }

        static SquadClass ClassOf(string key)
        {
            switch (ClassKey(key))
            {
                case "sniper": return SquadClass.Sniper;
                case "tank": return SquadClass.Assault;
                case "defender": return SquadClass.Heavy;
                case "antitank": return SquadClass.AntiTank;
                default: return SquadClass.Regular;
            }
        }

        /// <summary>Copies of a landing's loadout lines with the class defaults
        /// in the slots the editor left empty: a sniper gets the TAC-50, a tank
        /// and a defender the MG42, a defender the full UKB set with the
        /// exoskeleton and DefenderHealth, an anti-tank gunner a rifle (the LAW
        /// is his second weapon, never his first). A regular keeps the map's
        /// default kit exactly as before 6.17. The authored lines stay as they
        /// are: the same landing drops again.</summary>
        internal static List<RevivalComposition.CrewMan> WithClassDefaults(
            List<RevivalComposition.CrewMan> authored)
        {
            List<RevivalComposition.CrewMan> list = new List<RevivalComposition.CrewMan>();
            if (authored == null) return list;
            for (int i = 0; i < authored.Count; i++)
            {
                RevivalComposition.CrewMan a = authored[i];
                if (a == null) continue;
                RevivalComposition.CrewMan m = new RevivalComposition.CrewMan();
                m.Role = a.Role;
                m.Weapons = a.Weapons == null ? new int[0] : (int[])a.Weapons.Clone();
                m.Headwear = a.Headwear;
                m.Mask = a.Mask;
                m.Body = a.Body;
                m.Legs = a.Legs;
                m.Hands = a.Hands;
                m.Backpack = a.Backpack;
                m.Fpv = a.Fpv;
                m.Class = ClassKey(a.Class);
                switch (ClassOf(m.Class))
                {
                    case SquadClass.Sniper:
                        if (m.MainWeapon <= 0) m.Weapons = new int[] { SniperRifleId };
                        break;
                    case SquadClass.Assault:
                        if (m.MainWeapon <= 0) m.Weapons = new int[] { MachineGunId };
                        break;
                    case SquadClass.Heavy:
                        m.HealthScale = Mathf.Clamp(CfgDefenderHealth == null ? 1.5f
                            : CfgDefenderHealth.Value, 0.5f, 5f);
                        if (m.MainWeapon <= 0) m.Weapons = new int[] { MachineGunId };
                        if (m.Headwear <= 0 && m.Mask <= 0) m.Headwear = UkbHelmetId;
                        if (m.Body <= 0) m.Body = UkbBodyId;
                        if (m.Legs <= 0) m.Legs = UkbLegsId;
                        if (m.Hands <= 0) m.Hands = UkbHandsId;
                        if (m.Backpack <= 0) m.Backpack = ExoskeletonId;
                        break;
                    case SquadClass.AntiTank:
                        if (m.MainWeapon <= 0 || m.MainWeapon == LawId)
                            m.Weapons = new int[] { RifleFallbackId };
                        break;
                }
                list.Add(m);
            }
            return list;
        }

        /// <summary>The index of the spawn point an NPC came from. Crew names
        /// them "Crew&lt;i&gt;_&lt;role&gt;" and dresses point i with loadout
        /// line i % count; NPC_AI2.MySpawnPoint is the point (IL,
        /// NPC_AI2.InitBossData). The array position is the fallback.</summary>
        static int SpawnIndex(Component ai, int fallback)
        {
            try
            {
                Component sp = _fMySpawnPoint == null ? null : _fMySpawnPoint.GetValue(ai) as Component;
                string name = sp == null ? null : sp.gameObject.name;
                if (name != null && name.StartsWith("Crew"))
                {
                    int end = name.IndexOf('_');
                    int n;
                    if (end > 4 && int.TryParse(name.Substring(4, end - 4), out n) && n >= 0)
                        return n;
                }
            }
            catch { }
            return fallback;
        }

        static void Equip(Fighter f, RevivalComposition.CrewMan spec)
        {
            f.Class = ClassOf(spec == null ? "" : spec.Class);
            f.ArmorScale = ArmorScale(spec);
            f.RifleId = f.WeaponId;
            if (f.Class == SquadClass.AntiTank)
            {
                f.LawLeft = Mathf.Clamp(CfgAntiTankRockets == null ? 4 : CfgAntiTankRockets.Value, 0, 20);
                f.NextDrone = Time.time + 12f;
            }
        }

        static string ClassSummary(Squad s)
        {
            int[] n = new int[5];
            float armour = 0f;
            for (int i = 0; i < s.Men.Count; i++)
            {
                n[(int)s.Men[i].Class]++;
                armour += 1f - s.Men[i].ArmorScale;
            }
            return n[0] + " regular, " + n[1] + " sniper, " + n[2] + " tank, " + n[3]
                + " defender, " + n[4] + " anti-tank; armour takes "
                + (s.Men.Count == 0 ? 0f : armour / s.Men.Count * 100f).ToString("0")
                + " percent of a hit on average";
        }

        // ------------------------------------------------------------- armour

        /// <summary>What is left of a hit on a man in this loadout. The player's
        /// own rule (PlayerLifeDataManager.DecreaseDamageFromGearRegenerate,
        /// CONFIRMED IL): an item takes ItemRegenerate percent, doubled for a UKB
        /// part while the exoskeleton gives energy. An NPC hit carries no
        /// reliable body part (Turret.TryDamage always says Head), so the slots
        /// are weighted by the body they cover. HYPOTHESIS: the weights.</summary>
        static float ArmorScale(RevivalComposition.CrewMan spec)
        {
            if (spec == null) return 1f;
            bool exo = spec.Backpack == ExoskeletonId;
            float p = 0.18f * SlotProtection(spec.Headwear, exo)
                    + 0.04f * SlotProtection(spec.Mask, exo)
                    + 0.46f * SlotProtection(spec.Body, exo)
                    + 0.22f * SlotProtection(spec.Legs, exo)
                    + 0.10f * SlotProtection(spec.Hands, exo);
            float cap = Mathf.Clamp(CfgArmorMaxReduction == null ? 0.7f : CfgArmorMaxReduction.Value, 0f, 0.9f);
            return 1f - Mathf.Clamp(p, 0f, cap);
        }

        static float SlotProtection(int id, bool exo)
        {
            if (id <= 0) return 0f;
            float k = ItemRegenerate(id) / 100f;
            if (exo && IsUkbPart(id)) k *= 2f;
            return Mathf.Clamp01(k);
        }

        static readonly Dictionary<int, float> _regenerate = new Dictionary<int, float>();
        static MethodInfo _mItemScript, _mItemDb;

        /// <summary>ItemSpawned.Regenerate of an item, through
        /// ItemSpawnCategoriesDB.current.GetItemSpawnedScriptByID (IL). The UKB
        /// set falls back to research/items.tsv (4017/4316 48.5, 4509 40, 4603
        /// 0) when the database cannot be read.</summary>
        static float ItemRegenerate(int id)
        {
            float v;
            if (_regenerate.TryGetValue(id, out v)) return v;
            v = -1f;
            try
            {
                if (_mItemScript == null)
                {
                    Type db = RevivalPlugin.TypeByName("ItemSpawnCategoriesDB");
                    if (db != null)
                    {
                        _mItemDb = AccessTools.PropertyGetter(db, "current");
                        _mItemScript = AccessTools.Method(db, "GetItemSpawnedScriptByID",
                            new Type[] { typeof(int) }, null);
                    }
                }
                object current = _mItemDb == null ? null : _mItemDb.Invoke(null, null);
                object script = current == null || _mItemScript == null ? null
                    : _mItemScript.Invoke(current, new object[] { id });
                if (script != null)
                {
                    FieldInfo field = AccessTools.Field(script.GetType(), "Regenerate");
                    object raw = field == null ? null : field.GetValue(script);
                    if (raw != null) v = ToFloat(raw);
                }
            }
            catch { v = -1f; }
            if (v < 0f)
            {
                switch (id)
                {
                    case UkbHelmetId: case UkbBodyId: v = 48.5f; break;
                    case UkbLegsId: v = 40f; break;
                    case UkbHandsId: v = 0f; break;
                    default: return 0f;   // not cached: the database may load later
                }
            }
            _regenerate[id] = v;
            return v;
        }

        static MethodInfo _mUkbPart;
        static bool _ukbLooked;

        /// <summary>PlayerUKBController.UKB_IsPartOfEkzoskelet (IL: its four
        /// static id lists), or the four UKB-1 ids when that cannot answer.</summary>
        static bool IsUkbPart(int id)
        {
            if (id == UkbHelmetId || id == UkbBodyId || id == UkbLegsId || id == UkbHandsId) return true;
            if (!_ukbLooked)
            {
                _ukbLooked = true;
                Type t = RevivalPlugin.TypeByName("PlayerUKBController");
                _mUkbPart = t == null ? null : AccessTools.Method(t, "UKB_IsPartOfEkzoskelet",
                    new Type[] { typeof(int) }, null);
                if (_mUkbPart != null && (!_mUkbPart.IsStatic || _mUkbPart.ReturnType != typeof(bool)))
                    _mUkbPart = null;
            }
            if (_mUkbPart == null) return false;
            try { return (bool)_mUkbPart.Invoke(null, new object[] { id }); }
            catch { return false; }
        }

        /// <summary>A float, an int or an ObscuredFloat (implicit conversion);
        /// -1 when it is none of them.</summary>
        static float ToFloat(object raw)
        {
            if (raw == null) return -1f;
            if (raw is float) return (float)raw;
            if (raw is double) return (float)(double)raw;
            if (raw is int) return (int)raw;
            MethodInfo[] ms = raw.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != typeof(float)) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == raw.GetType())
                {
                    try { return (float)ms[i].Invoke(null, new object[] { raw }); }
                    catch { return -1f; }
                }
            }
            return -1f;
        }

        // ------------------------------------------------------------ install

        /// <summary>Two game hooks, both inert without an operation or a kill
        /// by damage owner 0.</summary>
        internal static void Install(Harmony harmony)
        {
            try
            {
                Type sType = RevivalPlugin.TypeByName("NPC_Settlement");
                MethodInfo stats = sType == null ? null : AccessTools.Method(sType, "StatsOnNpcKilled",
                    new Type[] { typeof(int) }, null);
                if (stats == null)
                    RevivalPlugin.L.LogWarning("NpcWar: NPC_Settlement.StatsOnNpcKilled(int) not found - "
                        + "the kill-streak guard is limited to squad rounds.");
                else
                    harmony.Patch(stats, new HarmonyMethod(typeof(NpcWar).GetMethod("KillStatsPrefix",
                        BindingFlags.Public | BindingFlags.Static)), null, null, null, null);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("NpcWar: kill-streak guard not installed - " + ex);
            }
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                MethodInfo apply = npc == null ? null : AccessTools.Method(npc, "ApplyDamage", null, null);
                ParameterInfo[] ps = apply == null ? null : apply.GetParameters();
                if (ps == null || ps.Length == 0 || ps[0].ParameterType != typeof(float))
                    RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2.ApplyDamage(float, ...) not found - "
                        + "squad armour and the defender's kneeling window do not change damage.");
                else
                {
                    harmony.Patch(apply, new HarmonyMethod(typeof(NpcWar).GetMethod("ApplyDamagePrefix",
                        BindingFlags.Public | BindingFlags.Static)), null, null, null, null);
                    RevivalPlugin.L.LogInfo("NpcWar: squad armour and kill-streak guard installed.");
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("NpcWar: squad armour not installed - " + ex);
            }
        }

        /// <summary>Prefix on NPC_Settlement.StatsOnNpcKilled(killerId). Every
        /// plugin kill carries owner 0 - squad rounds, the drone and LAW
        /// blasts, turrets and patrol guns - and a settlement-clearing streak of
        /// owner 0 reads PhotonPlayer.Find(0).ID and throws (RE 35). Resetting
        /// an owner-0 streak to -1 here, where the streak is counted, covers
        /// every one of them; a player's streak is never touched.</summary>
        public static void KillStatsPrefix(object __instance, int __0)
        {
            if (__0 != 0 || __instance == null) return;
            try
            {
                if (!_lastKillerLooked)
                {
                    _lastKillerLooked = true;
                    _fLastKillerId = AccessTools.Field(__instance.GetType(), "_lastKillerId");
                    if (_fLastKillerId != null && _fLastKillerId.FieldType != typeof(int)) _fLastKillerId = null;
                }
                if (_fLastKillerId != null && (int)_fLastKillerId.GetValue(__instance) == 0)
                    _fLastKillerId.SetValue(__instance, -1);
            }
            catch { }
        }

        /// <summary>Prefix on NPC_AI2.ApplyDamage: a squad man's armour takes
        /// its share of every hit (players, NPCs, blasts), and a defender takes
        /// DefenderRegenDamage times as much while he kneels. ApplyDamage only
        /// damages on the owner, so a changed value elsewhere changes nothing.</summary>
        public static void ApplyDamagePrefix(object __instance, ref float __0)
        {
            if (_armoured.Count == 0 || __0 <= 0f) return;
            try
            {
                Component c = __instance as Component;
                if (c == null) return;
                Fighter f;
                if (!_armoured.TryGetValue(c.GetInstanceID(), out f)) return;
                float k = f.ArmorScale;
                if (f.Class == SquadClass.Heavy && IntField(f.Ai, _fMainState, -1) == MainRegen)
                    k *= Mathf.Clamp(CfgDefenderRegenDamage == null ? 1.3f : CfgDefenderRegenDamage.Value, 1f, 5f);
                __0 *= k;
            }
            catch { }
        }

        /// <summary>Is this NPC weapon controller a squad anti-tank gunner's?
        /// CrewLaw then detonates the player LAW's blast (900 in 12), which
        /// VehicleArmor recognises, instead of the patrol crew value.</summary>
        internal static bool SquadLaw(object weaponController, out float damage, out float radius)
        {
            damage = SquadLawDamage;
            radius = SquadLawRadius;
            if (weaponController == null || _squads.Count == 0) return false;
            for (int q = 0; q < _squads.Count; q++)
                for (int i = 0; i < _squads[q].Men.Count; i++)
                {
                    Fighter m = _squads[q].Men[i];
                    if (m.Class != SquadClass.AntiTank || m.Ai == null) continue;
                    Component w = WeaponOf(m);
                    if (w != null && (object)w == weaponController) return true;
                }
            return false;
        }

        // ------------------------------------------------- defender regeneration

        static bool Regenerating(Fighter f, float now)
        {
            if (f.Class != SquadClass.Heavy) return false;
            return IntField(f.Ai, _fMainState, -1) == MainRegen || now < f.RegenHoldUntil;
        }

        static bool RegenDue(Fighter f, float now)
        {
            if (f.Class != SquadClass.Heavy || _mStartRegen == null || now < f.RegenCheck) return false;
            f.RegenCheck = now + 0.25f;
            int allowed = Mathf.Clamp(CfgDefenderRegenCount == null ? 2 : CfgDefenderRegenCount.Value, 0, 10);
            if (f.RegenUsed >= allowed || Reloading(f) || !IsMine(f.Ai)) return false;
            float below = Mathf.Clamp(CfgDefenderRegenBelow == null ? 0.5f : CfgDefenderRegenBelow.Value, 0.1f, 0.9f);
            float left = HealthFraction(f);
            return left > 0f && left < below;
        }

        /// <summary>The game's own boss regeneration (IL): usedCount+1, a
        /// quarter of HealthMax to heal in ten steps a second apart, the RPC,
        /// SetStateWithAnimAndSync(Regeneration) with the ukb_boss_regen_start /
        /// _idle clips and a 12 s pause; RegenerationActions heals and returns
        /// him to Idle, ukb_boss_regen_end on the way out.</summary>
        static bool StartRegen(Fighter f, float now)
        {
            try
            {
                ReleaseAim(f);
                f.HasOrder = false;
                f.Stance = Stance.Hold;
                float before = HealthFraction(f);
                _mStartRegen.Invoke(f.Ai, null);
                f.RegenUsed++;
                f.RegenHoldUntil = now + 1.5f;
                f.WantMain = MainRegen;
                f.WantAdd = AddNone;
                RevivalPlugin.L.LogInfo("NpcWar: " + (f.Squad == null ? "" : f.Squad.Tag + " ")
                    + "defender " + f.Ai.name + " kneels to regenerate at "
                    + (before * 100f).ToString("0") + " percent health (" + f.RegenUsed + ").");
                return true;
            }
            catch (Exception ex)
            {
                f.RegenUsed = 1000;
                RevivalPlugin.L.LogWarning("NpcWar: regeneration failed on " + f.Ai.name + " - "
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return false;
            }
        }

        // ------------------------------------------------------ anti-tank gunner

        static float AntiTankSelfDefense()
        {
            return Mathf.Clamp(CfgAntiTankSelfDefense == null ? 35f : CfgAntiTankSelfDefense.Value, 5f, 200f);
        }

        /// <summary>The anti-tank gunner's own business before the ordinary
        /// turn. True when it has taken his turn.</summary>
        static bool AntiTankStep(Fighter f, Squad s, Vector3 front, Vector3 centre, float now)
        {
            // 1  His drone is in the air: he flies it, kneeling, facing its target.
            if (f.DroneId > 0)
            {
                if (CrewDrone.InFlight(f.DroneId) && now < f.DroneHoldUntil)
                {
                    OperateDrone(f, now);
                    return true;
                }
                f.DroneId = 0;
                f.DroneTarget = null;
            }

            Component vehicle = s.Vehicle;
            bool vehicleAlive = vehicle != null && VehicleAlive(vehicle);
            if (vehicleAlive)
            {
                Transform hull = vehicle.transform;
                float d = Flat(hull.position - f.Tr.position);
                // 2  The drone goes in first ...
                if (CanLaunch(f, now) && d > 25f && d < 350f)
                {
                    LaunchDrone(f, s, vehicle.gameObject, VehicleAim, 0.5f, "vehicle", now);
                    return true;
                }
                // 3  ... then he runs in with the LAW.
                if (f.LawLeft > 0 && !Reloading(f)) return LawAttack(f, s, vehicle, d, now);
                if (f.LawLeft > 0)
                {
                    f.Stance = Stance.Hold;
                    f.Target = hull;
                    Face(f);
                    return true;
                }
            }
            // No vehicle, or no rocket left: the rifle again.
            if (f.SwitchPhase == 0 && f.RifleId > 0 && f.RifleId != LawId
                && CurrentItem(f) == LawId && !Reloading(f))
                BeginSwitch(f, f.RifleId, now);

            // 4  The drone at the enemy the squad is fighting, never into the
            //    middle of his own men.
            if (CanLaunch(f, now) && s.Threat != null && s.ThreatSeen && !vehicleAlive)
            {
                Component npc = s.Threat.GetComponent(_npcType);
                Fighter other = npc == null ? null : FighterOf(npc);
                float d = Flat(s.Threat.position - f.Tr.position);
                float blast = RevivalPlugin.CfgDroneRadius == null ? 7f : RevivalPlugin.CfgDroneRadius.Value;
                if (npc != null && Alive(npc) && (other == null || other.Squad != s)
                    && d > 40f && d < 300f && !MateNear(s, s.Threat.position, blast + 8f))
                {
                    LaunchDrone(f, s, s.Threat.gameObject, NpcDroneAim, 1.5f, "NPC", now);
                    return true;
                }
            }
            return false;
        }

        static bool CanLaunch(Fighter f, float now)
        {
            int allowed = Mathf.Clamp(CfgAntiTankDrones == null ? 3 : CfgAntiTankDrones.Value, 0, 20);
            return DronesEnabled && f.DronesUsed < allowed && now >= f.NextDrone && f.DroneId == 0;
        }

        static void LaunchDrone(Fighter f, Squad s, GameObject target, float aimUp, float miss,
                                string what, float now)
        {
            float seconds = Mathf.Clamp(CfgAntiTankDroneSeconds == null ? 45f : CfgAntiTankDroneSeconds.Value, 5f, 600f);
            f.NextDrone = now + seconds;
            if (target == null) return;
            // The player FPV drone's blast: VehicleArmor knows it (a tank takes
            // TankFpvHits), and against infantry it is what a real one does.
            float damage = RevivalPlugin.CfgDroneDamage == null ? 550f : RevivalPlugin.CfgDroneDamage.Value;
            float radius = RevivalPlugin.CfgDroneRadius == null ? 7f : RevivalPlugin.CfgDroneRadius.Value;
            Vector3 from = f.Tr.position + Vector3.up * 3.2f + f.Tr.forward * 1.5f;
            int id = 0;
            try { id = CrewDrone.LaunchAt(from, target, aimUp, miss, damage, radius, what); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("NpcWar: FPV launch failed - " + ex.Message);
            }
            if (id <= 0) return;
            f.DroneId = id;
            f.DronesUsed++;
            s.Drones++;
            f.DroneTarget = target.transform;
            f.DroneHoldUntil = now + 40f;
            f.HasOrder = false;
            if (f.IkDriven) ReleaseAim(f);
        }

        /// <summary>Kneel and face the drone's target while it flies.</summary>
        static void OperateDrone(Fighter f, float now)
        {
            f.Stance = Stance.Hold;
            f.HasOrder = false;
            if (f.IkDriven) ReleaseAim(f);
            Transform keep = f.Target;
            if (f.DroneTarget != null) f.Target = f.DroneTarget;
            Drive(f, MainIdle, AddNone, PoseCrouch, now, true);
            Face(f);
            f.Target = keep;
        }

        /// <summary>Draw the LAW, run into range with a line of fire, fire.</summary>
        static bool LawAttack(Fighter f, Squad s, Component vehicle, float d, float now)
        {
            Transform hull = vehicle.transform;
            float range = Mathf.Clamp(CfgAntiTankLawRange == null ? 90f : CfgAntiTankLawRange.Value,
                SquadLawRadius * 2f + 12f, 250f);
            Vector3 to = FlatV(hull.position - f.Tr.position);
            Vector3 dir = to.sqrMagnitude < 0.01f ? f.Tr.forward : to.normalized;
            Vector3 standoff = new Vector3(hull.position.x, f.Tr.position.y, hull.position.z)
                - dir * Mathf.Min(range * 0.75f, Mathf.Max(0f, to.magnitude - 1f));

            if (CurrentItem(f) != LawId || f.SwitchPhase != 0)
            {
                if (f.SwitchPhase == 0) BeginSwitch(f, LawId, now);
                if (d > range) RunTo(f, standoff, now);
                else { f.Target = hull; Hold(f, null, now); Face(f); }
                return true;
            }

            Vector3 eye = f.Tr.position + Vector3.up * EyeHeight;
            bool open = VehicleClear(eye, hull.position + Vector3.up * VehicleAim, vehicle);
            if (d > range)
            {
                RunTo(f, standoff, now);
                return true;
            }
            if (!open)
            {
                // In range but something in the way: a few steps closer.
                RunTo(f, f.Tr.position + dir * Mathf.Min(15f, d * 0.5f), now);
                return true;
            }
            f.Target = hull;
            f.TargetIsPlayer = false;
            f.Sees = true;
            f.AimHeight = VehicleAim;
            f.LastSeen = now;
            if (!f.Armed)
            {
                Hold(f, null, now);
                Face(f);
                return true;
            }
            Fire(f, now);
            return true;
        }

        /// <summary>Run to a point, with the same smooth re-aiming as the line.</summary>
        static void RunTo(Fighter f, Vector3 dest, float now)
        {
            if (f.IkDriven) ReleaseAim(f);
            if (Flat(dest - f.Tr.position) <= LaneSlack)
            {
                Hold(f, null, now);
                return;
            }
            f.Stance = Stance.Reposition;
            int live = IntField(f.Ai, _fMainState, -1);
            if (f.HasOrder && live == MainRun && StillOurPoint(f) && now < f.MoveDeadline)
            {
                if (Flat(dest - f.Ordered) < RetargetSlack) return;
                if (now - f.LastFullOrder < FullOrderSeconds && Retarget(f, dest, now)) return;
            }
            bool stopped = f.HasOrder && live == MainIdle && now - f.LastFullOrder > 0.6f;
            if (now < f.NextMove && !stopped) return;
            f.NextMove = now + 1f;
            f.LastFullOrder = now;
            OrderMove(f, dest, MainRun, AddNone, PoseStand);
        }

        static bool MateNear(Squad s, Vector3 point, float radius)
        {
            float sqr = radius * radius;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter m = s.Men[i];
                if (m.Ai == null || m.Tr == null || !Alive(m.Ai)) continue;
                if ((m.Tr.position - point).sqrMagnitude < sqr) return true;
            }
            return false;
        }

        // ------------------------------------------------------ weapon switch

        static int CurrentItem(Fighter f)
        {
            if (f.Wm == null && _fWeaponsManager != null && f.Ai != null)
            {
                try { f.Wm = _fWeaponsManager.GetValue(f.Ai) as Component; }
                catch { }
            }
            return IntField(f.Wm, _fWeaponItem, -1);
        }

        /// <summary>Put the weapon in hand away (NPC_AI2.EquipWeapon(false):
        /// NPC_WeaponsManager.HideWeapon -> NetworkShowWeapon's hide, which
        /// destroys the model and ends in ClearWeaponState - IL), then draw the
        /// other through SetMainWeaponId(id, true), the path every first draw
        /// takes. Both are RPCs, so every client sees the change.</summary>
        static void BeginSwitch(Fighter f, int id, float now)
        {
            if (id <= 0 || f.SwitchPhase != 0 || _mEquipWeapon == null || _mSetMainWeaponId == null) return;
            if (f.Ai == null || !Alive(f.Ai) || !IsMine(f.Ai) || Reloading(f)) return;
            if (CurrentItem(f) == id && ReadArmed(f)) { f.WeaponId = id; return; }
            f.SwitchTo = id;
            f.SwitchSince = now;
            f.SwitchPhase = 1;
            ReleaseAim(f);
            try { _mEquipWeapon.Invoke(f.Ai, new object[] { false, true }); }
            catch (Exception ex)
            {
                f.SwitchPhase = 0;
                if (now >= f.NextSwitchWarn)
                {
                    f.NextSwitchWarn = now + 30f;
                    RevivalPlugin.L.LogWarning("NpcWar: putting the weapon away failed on " + f.Ai.name
                        + " - " + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                }
            }
        }

        /// <summary>Advance a weapon switch. True while it is under way.</summary>
        static bool WeaponSwitch(Fighter f, float now)
        {
            if (f.SwitchPhase == 0) return false;
            if (f.Ai == null || !Alive(f.Ai)) { f.SwitchPhase = 0; return false; }
            if (f.SwitchPhase == 1)
            {
                // The old weapon goes away and frees slot 0.
                if (IntField(f.Wm, _fWeaponSlot, -1) == 0 && now - f.SwitchSince < 3f) return true;
                f.WeaponId = f.SwitchTo;
                f.SwitchPhase = 2;
                f.SwitchSince = now;
                try { _mSetMainWeaponId.Invoke(f.Ai, new object[] { f.SwitchTo, true }); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("NpcWar: drawing " + f.SwitchTo + " failed on " + f.Ai.name
                        + " - " + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                }
                return true;
            }
            if (ReadArmed(f) && CurrentItem(f) == f.SwitchTo)
            {
                f.SwitchPhase = 0;
                f.NextState = 0f;
                if (CfgDebug.Value)
                    RevivalPlugin.L.LogInfo("NpcWar: " + f.Ai.name + " now holds " + f.SwitchTo + ".");
                return false;
            }
            if (now - f.SwitchSince < 6f) return true;
            f.SwitchPhase = 0;
            if (now >= f.NextSwitchWarn)
            {
                f.NextSwitchWarn = now + 30f;
                RevivalPlugin.L.LogWarning("NpcWar: weapon switch to " + f.SwitchTo + " on " + f.Ai.name
                    + " did not finish (item " + CurrentItem(f) + ", slot " + IntField(f.Wm, _fWeaponSlot, -1)
                    + ") - the draw is retried.");
            }
            return false;
        }

        // ---------------------------------------------------- smooth movement

        static NavMeshAgent Agent(Fighter f)
        {
            if (_fNavAgent == null || f.Ai == null) return null;
            try
            {
                NavMeshAgent a = _fNavAgent.GetValue(f.Ai) as NavMeshAgent;
                return a == null ? null : a;
            }
            catch { return null; }
        }

        /// <summary>Move the man's walk point and his NavMeshAgent's destination
        /// without a new order. SetDestination keeps the agent moving; only the
        /// game's NavAgentMoveToPos stops it first. False when the agent cannot
        /// take it; the caller then gives a full order.</summary>
        static bool Retarget(Fighter f, Vector3 dest, float now)
        {
            NavMeshAgent a = Agent(f);
            if (a == null || f.Point == null) return false;
            try
            {
                if (!a.isActiveAndEnabled || !a.isOnNavMesh || a.pathPending) return false;
                Vector3 target = Ground(dest);
                f.Point.transform.position = target;
                if (!a.SetDestination(target)) return false;
                if (a.isStopped) a.isStopped = false;
                f.Ordered = dest;
                f.MoveDeadline = now + 6f + Flat(dest - f.Tr.position) * 0.25f;
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ vehicles

        static FieldInfo _fPassengers, _fDurability;
        static bool _vehicleLooked;
        static readonly Dictionary<string, object> _sideFaction = new Dictionary<string, object>();

        static bool IsSquadVehicle(Fighter f, Transform t)
        {
            return t != null && f.Squad != null && f.Squad.Vehicle != null && t == f.Squad.Vehicle.transform;
        }

        static bool VehicleVisible(Fighter f, Transform hull, out float height)
        {
            height = VehicleAim;
            Component v = f.Squad == null ? null : f.Squad.Vehicle;
            if (v == null || hull == null) return false;
            Vector3 eye = f.Tr.position + Vector3.up * (f.Crouched ? CrouchEye : EyeHeight);
            return VehicleClear(eye, hull.position + Vector3.up * VehicleAim, v);
        }

        /// <summary>Clear, with any collider of the vehicle itself counting as
        /// open: its colliders need not hang under the transform we aim at.</summary>
        static bool VehicleClear(Vector3 from, Vector3 to, Component vehicle)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 1f) return true;
            dir /= dist;
            Vector3 point;
            GameObject hit = Turret.RaycastObject(from + dir * 1.2f, dir, dist, out point);
            if (hit == null) return true;
            if (vehicle == null) return false;
            if (hit.transform.IsChildOf(vehicle.transform)) return true;
            Component owner = hit.GetComponentInParent(vehicle.GetType());
            return owner == vehicle || (to - point).sqrMagnitude < 9f;
        }

        /// <summary>The nearest vehicle inside 1.5 x AssaultRange of the squad
        /// that its faction would attack: a patrol or convoy vehicle with its
        /// crew aboard whose side the squad hates, or a vehicle with a player
        /// aboard the squad's own AI treats as an enemy.</summary>
        static Component HostileVehicle(Squad s, Vector3 centre)
        {
            Component[] all = VehicleScan.All();
            if (all.Length == 0) return null;
            float reach = AssaultRange() * 1.5f;
            float bestSqr = reach * reach;
            Component best = null;
            for (int i = 0; i < all.Length; i++)
            {
                Component v = all[i];
                if (v == null) continue;
                float d = (v.transform.position - centre).sqrMagnitude;
                if (d >= bestSqr || !VehicleAlive(v) || !VehicleHostile(s, v)) continue;
                best = v;
                bestSqr = d;
            }
            if (best != null && best != s.Vehicle)
                RevivalPlugin.L.LogInfo("NpcWar: " + s.Tag + " engages the vehicle " + best.gameObject.name
                    + " " + Mathf.Sqrt(bestSqr).ToString("0") + " units away.");
            return best;
        }

        static void LookUpVehicle(Type t)
        {
            if (_vehicleLooked || t == null) return;
            _vehicleLooked = true;
            _fPassengers = AccessTools.Field(t, "Passengers");
            _fDurability = AccessTools.Field(t, "Durability");
            if (_fPassengers == null || _fDurability == null)
                RevivalPlugin.L.LogWarning("NpcWar: VehicleGameSystem.Passengers or Durability missing - "
                    + "squads may ignore vehicles or keep firing at wrecks.");
        }

        static bool VehicleAlive(Component v)
        {
            if (v == null || !v.gameObject.activeInHierarchy) return false;
            LookUpVehicle(v.GetType());
            if (_fDurability == null) return true;
            try
            {
                float left = ToFloat(_fDurability.GetValue(v));
                return left < 0f ? true : left > 0f;
            }
            catch { return true; }
        }

        static bool VehicleHostile(Squad s, Component v)
        {
            string side = null;
            try { side = Patrol.CrewedSide(v); }
            catch { side = null; }
            if (side != null)
            {
                object faction = SideFaction(side);
                if (faction == null) return false;
                for (int m = 0; m < s.Men.Count; m++)
                    if (s.Men[m].Ai != null && Hostile(s.Men[m].Hated, faction)) return true;
                return false;
            }
            LookUpVehicle(v.GetType());
            if (_fPassengers == null) return false;
            Array seats = null;
            try { seats = _fPassengers.GetValue(v) as Array; }
            catch { seats = null; }
            if (seats == null) return false;
            for (int i = 0; i < seats.Length; i++)
            {
                object o = seats.GetValue(i);
                GameObject go = o as GameObject;
                if (go == null)
                {
                    Component c = o as Component;
                    if (c != null) go = c.gameObject;
                }
                if (go == null || go.GetComponent(_npcType) != null) continue;
                if (PlayerHostile(s, go)) return true;
            }
            return false;
        }

        static object SideFaction(string side)
        {
            object faction;
            if (_sideFaction.TryGetValue(side, out faction)) return faction;
            faction = null;
            try
            {
                object opts = Fraktion.Optionen(side);
                faction = opts == null ? null : _fMyFraction.GetValue(opts);
            }
            catch { faction = null; }
            _sideFaction[side] = faction;
            return faction;
        }

        /// <summary>NPC_AI2.IsEnemyFraction(player) of a living squad man - the
        /// same test the game makes before it fights a player.</summary>
        static bool PlayerHostile(Squad s, GameObject player)
        {
            if (_mIsEnemy == null || player == null) return false;
            Type want = _mIsEnemy.GetParameters()[0].ParameterType;
            object arg = null;
            if (want == typeof(GameObject)) arg = player;
            else if (typeof(Component).IsAssignableFrom(want)) arg = player.GetComponent(want);
            else if (want.IsInstanceOfType(player)) arg = player;
            if (arg == null) return false;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter m = s.Men[i];
                if (m.Ai == null || !Alive(m.Ai)) continue;
                try
                {
                    object r = _mIsEnemy.Invoke(m.Ai, new object[] { arg });
                    return r is bool && (bool)r;
                }
                catch { return false; }
            }
            return false;
        }

        // ------------------------------------------------------------- removal

        /// <summary>The squad leaves the map: its LIVING men through
        /// PhotonNetwork.Destroy on the master, so every client loses the same
        /// objects. The dead stay where they fell for CorpseMinutes with their
        /// settlement, whose children they are (6.17: the 6.16.7 log removed 15
        /// bodies with their loot on "wiped out").</summary>
        static void Remove(Squad s, string why)
        {
            int gone = 0;
            Grave grave = new Grave();
            grave.Tag = s.Tag;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Point != null) UnityEngine.Object.Destroy(f.Point);
                if (f.Ai == null) continue;
                if (Alive(f.Ai)) { if (NetDestroy(f.Ai.gameObject)) gone++; }
                else grave.Bodies.Add(f.Ai.gameObject);
            }
            List<int> stale = new List<int>();
            foreach (KeyValuePair<int, Fighter> pair in _armoured)
                if (pair.Value == null || pair.Value.Squad == s) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) _armoured.Remove(stale[i]);
            _squads.Remove(s);

            float minutes = Mathf.Clamp(CfgCorpseMinutes == null ? 20f : CfgCorpseMinutes.Value, 1f, 120f);
            if (grave.Bodies.Count > 0)
            {
                grave.Settlement = s.Settlement;
                grave.Until = Time.time + minutes * 60f;
                _graves.Add(grave);
            }
            else if (s.Settlement != null)
            {
                Crew.Forget(s.Settlement);
                UnityEngine.Object.Destroy(s.Settlement);
            }
            RevivalPlugin.L.LogInfo("NpcWar: operation " + s.Tag + " ended (" + why
                + ") - " + gone + " living men removed from the map, " + grave.Bodies.Count
                + " dead stay " + (grave.Bodies.Count > 0 ? minutes.ToString("0") + " min for their loot" : "")
                + "; " + s.Shots + " shots at NPCs, " + s.Hits + " hits, " + s.Drones + " drone(s), "
                + s.Rockets + " LAW rocket(s).");
        }

        /// <summary>The dead of ended operations go once their time is up and
        /// no player is standing among them.</summary>
        static void TickGraves(float now)
        {
            for (int i = _graves.Count - 1; i >= 0; i--)
            {
                Grave g = _graves[i];
                if (now < g.Until) continue;
                for (int b = 0; b < g.Bodies.Count; b++)
                {
                    GameObject go = g.Bodies[b];
                    if (go != null && PlayerNear(go.transform.position, PlayerNearCorpse))
                    {
                        g.Until = now + 60f;
                        break;
                    }
                }
                if (now < g.Until) continue;
                int n = 0;
                for (int b = 0; b < g.Bodies.Count; b++)
                    if (g.Bodies[b] != null && NetDestroy(g.Bodies[b])) n++;
                if (g.Settlement != null)
                {
                    Crew.Forget(g.Settlement);
                    UnityEngine.Object.Destroy(g.Settlement);
                }
                _graves.RemoveAt(i);
                RevivalPlugin.L.LogInfo("NpcWar: the dead of operation " + g.Tag + " are gone ("
                    + n + " bodies removed).");
            }
        }

        static PropertyInfo _ngsInstance;
        static FieldInfo _ngsPlayers;
        static bool _ngsLooked;

        /// <summary>Is any player (NetworkGameServer.Instance.NetworkPlayers, the
        /// list SurvNpcFire reads) within r of a point?</summary>
        static bool PlayerNear(Vector3 p, float r)
        {
            if (!_ngsLooked)
            {
                _ngsLooked = true;
                Type t = RevivalPlugin.TypeByName("NetworkGameServer");
                _ngsInstance = t == null ? null : t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _ngsPlayers = t == null ? null : AccessTools.Field(t, "NetworkPlayers");
            }
            try
            {
                object server = _ngsInstance == null ? null : _ngsInstance.GetValue(null, null);
                IList players = server == null || _ngsPlayers == null ? null : _ngsPlayers.GetValue(server) as IList;
                if (players == null) return false;
                float sqr = r * r;
                for (int i = 0; i < players.Count; i++)
                {
                    GameObject go = players[i] as GameObject;
                    if (go == null)
                    {
                        Component c = players[i] as Component;
                        if (c != null) go = c.gameObject;
                    }
                    if (go != null && (go.transform.position - p).sqrMagnitude < sqr) return true;
                }
            }
            catch { }
            return false;
        }

        static bool NetDestroy(GameObject go)
        {
            if (go == null) return false;
            try
            {
                if (_mDestroy != null && IsMine(go.GetComponent(_npcType)))
                {
                    _mDestroy.Invoke(null, new object[] { go });
                    return true;
                }
                UnityEngine.Object.Destroy(go);
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("NpcWar: removing a squad man failed - "
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return false;
            }
        }

        // ----------------------------------------------------- faction helpers

        static bool OtherFaction(object own, object other)
        {
            return own != null && other != null && !own.Equals(other);
        }

        static object FactionOf(Component ai)
        {
            object opt = MainOptions(ai);
            if (opt == null) return null;
            try { return _fMyFraction.GetValue(opt); }
            catch { return null; }
        }

        static Array GetHated(Component ai)
        {
            object opt = MainOptions(ai);
            if (opt == null) return null;
            try { return _fHated.GetValue(opt) as Array; }
            catch { return null; }
        }

        static object MainOptions(Component ai)
        {
            if (ai == null || _fMainOptions == null) return null;
            try { return _fMainOptions.GetValue(ai); }
            catch { return null; }
        }

        /// <summary>Is B's faction in A's HatedFractions? The same test as
        /// NPC_AI2.IsEnemyFraction, between two NPCs.</summary>
        static bool Hostile(Array aHated, object bFrac)
        {
            if (aHated == null || bFrac == null) return false;
            for (int i = 0; i < aHated.Length; i++)
            {
                object h = aHated.GetValue(i);
                if (h != null && h.Equals(bFrac)) return true;
            }
            return false;
        }

        // -------------------------------------------------------- npc helpers

        static List<Component> LiveNpcs()
        {
            List<Component> list = new List<Component>();
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_npcType);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c != null && Alive(c)) list.Add(c);
            }
            return list;
        }

        static bool Alive(Component ai)
        {
            if (ai == null) return false;
            try
            {
                object r = _mIsAlive.Invoke(ai, null);
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        /// <summary>The player this NPC's own AI is fighting, or null.</summary>
        static Component KillTarget(Fighter f)
        {
            if (_fKillTarget == null || f.Ai == null) return null;
            try
            {
                Component c = _fKillTarget.GetValue(f.Ai) as Component;
                return c == null ? null : c;   // Unity null for a destroyed player
            }
            catch { return null; }
        }

        static bool HasKillTarget(Fighter f) { return KillTarget(f) != null; }

        /// <summary>Health left, 0..1. NPC_AI2.Specifications carries Health and
        /// HealthMax as floats (Crew reads the same pair). 1 when they cannot be
        /// read, so an unreadable NPC simply behaves like an unhurt one.</summary>
        static float HealthFraction(Fighter f)
        {
            if (_fSpecs == null || f.Ai == null) return 1f;
            try
            {
                object specs = _fSpecs.GetValue(f.Ai);
                if (specs == null) return 1f;
                if (_fHealth == null) _fHealth = AccessTools.Field(specs.GetType(), "Health");
                if (_fHealthMax == null) _fHealthMax = AccessTools.Field(specs.GetType(), "HealthMax");
                if (_fHealth == null || _fHealthMax == null) return 1f;
                float have = Convert.ToSingle(_fHealth.GetValue(specs));
                float full = Convert.ToSingle(_fHealthMax.GetValue(specs));
                return full <= 0.01f ? 1f : Mathf.Clamp01(have / full);
            }
            catch { return 1f; }
        }

        /// <summary>HealthMax of a man, or -1 when it cannot be read.</summary>
        static float HealthMax(Fighter f)
        {
            if (_fSpecs == null || f.Ai == null) return -1f;
            try
            {
                object specs = _fSpecs.GetValue(f.Ai);
                if (specs == null) return -1f;
                if (_fHealthMax == null) _fHealthMax = AccessTools.Field(specs.GetType(), "HealthMax");
                if (_fHealthMax == null) return -1f;
                return Convert.ToSingle(_fHealthMax.GetValue(specs));
            }
            catch { return -1f; }
        }

        /// <summary>The DamagePerShot a defender's round on a squad man is
        /// sent with. Turret.TryDamage passes damagePart 0 (Head) and damageType
        /// 0, and NPC_AI2.CalculateDamageValueFromDamageData triples a Head hit
        /// unless the type is 17/18 (CONFIRMED IL): 18 takes 54. A 120 point
        /// squad man therefore survived two 6.16.6 defender hits with 12 left -
        /// under 25, the vanilla wounded state, in which ApplyDamage ignores
        /// further hits. The round is therefore sized so that exactly
        /// DefenderHitsToKill hits kill a man at full health, read from his own
        /// HealthMax, so a changed Patrol/CrewHealth keeps the rule.
        /// 6.19: three hits, not two - the field report says no party can be put
        /// together that wipes a settlement. The old Mathf.Max against the
        /// configured DamagePerShot had to go with it: 18 configured takes 54 a
        /// hit, which would leave a 120 point man at 12 after two - back in the
        /// wounded band the derivation exists to avoid. A defender round is now
        /// the derived value alone (40 of 120 points a hit), and DamagePerShot
        /// keeps its meaning for every OTHER shooter, the squad included.
        /// A squad round (54) kills a defender of up to 108 points in two;
        /// settlement NPCs take their points from NPC_SpawnPoint.Health in the
        /// scene (the constructor's own value is 150), which is not measured
        /// here. Armour still scales the hit afterwards (ApplyDamagePrefix), so
        /// a well-dressed man takes more than three.</summary>
        static float DefenderRound(Fighter man, float configured)
        {
            float full = HealthMax(man);
            if (full <= 0f) return configured;
            return (full + 1f) / (TryDamageHead * DefenderHitsToKill);
        }

        static FieldInfo _fLastKillerId;
        static bool _lastKillerLooked;

        /// <summary>Our rounds carry damageOwnerId 0. NPC_Settlement.StatsOnNpcKilled
        /// counts consecutive kills by one id (_lastKillerId, _killedCount) and,
        /// in a settlement with six or more spawn points, on the kill that leaves
        /// no ready NPC after spawnPoints-1 kills in a row, reads
        /// PhotonPlayer.Find(_lastKillerId).ID for an achievement: a
        /// NullReferenceException for id 0 (CONFIRMED IL IL_00B6..IL_00CE and the
        /// 6.16.6 runtime log). The NPC is already dead by then (DecreaseHealth
        /// -> SetHealthValue -> DeathAction run first), but before 6.16.7 the
        /// throw ended the whole operation: the squad vanished right after the
        /// last defender fell (E-057). Resetting an id-0 streak to -1 makes
        /// every one of our kills a "new killer" (IL_0115, count 1); a player's
        /// streak is never touched. _lastKillerId is read and written only in
        /// the constructor and StatsOnNpcKilled (CONFIRMED IL scan). A real
        /// player id is NOT an option: kill credit, counter-attack and
        /// settlement hostility would all go to that player.</summary>
        static void BreakKillStreak(Component ai)
        {
            if (_fMySettlement == null || ai == null) return;
            try
            {
                object home = _fMySettlement.GetValue(ai);
                if (home == null) return;
                if (!_lastKillerLooked)
                {
                    _lastKillerLooked = true;
                    _fLastKillerId = AccessTools.Field(home.GetType(), "_lastKillerId");
                    if (_fLastKillerId != null && _fLastKillerId.FieldType != typeof(int)) _fLastKillerId = null;
                    if (_fLastKillerId == null)
                        RevivalPlugin.L.LogWarning("NpcWar: NPC_Settlement._lastKillerId missing - "
                            + "a settlement-clearing kill may throw (caught).");
                }
                if (_fLastKillerId != null && (int)_fLastKillerId.GetValue(home) == 0)
                    _fLastKillerId.SetValue(home, -1);
            }
            catch { }
        }

        static bool Reloading(Fighter f)
        {
            if (_fReloading == null || f.Ai == null) return false;
            try { object v = _fReloading.GetValue(f.Ai); return v is bool && (bool)v; }
            catch { return false; }
        }

        static Component WeaponOf(Fighter f)
        {
            if (_fWeapon == null || f.Ai == null) return null;
            try
            {
                Component c = _fWeapon.GetValue(f.Ai) as Component;
                return c == null ? null : c;
            }
            catch { return null; }
        }

        static Vector3 Muzzle(Component weapon, Fighter f)
        {
            if (weapon != null && _mMuzzle != null)
            {
                try
                {
                    object v = _mMuzzle.Invoke(weapon, null);
                    if (v is Vector3 && (Vector3)v != Vector3.zero) return (Vector3)v;
                }
                catch { }
            }
            return Vector3.zero;
        }

        static int IntField(Component c, FieldInfo f, int fallback)
        {
            if (c == null || f == null) return fallback;
            try
            {
                object v = f.GetValue(c);
                return v == null ? fallback : Convert.ToInt32(v);
            }
            catch { return fallback; }
        }

        /// <summary>May a squad shoot this scene NPC? Owned here, not god-moded,
        /// not in a safe settlement (traders), not in a conversation, and one
        /// that NPC_AI2.ApplyDamage really hurts (Hurtable).</summary>
        static bool Targetable(Component ai)
        {
            if (!IsMine(ai)) return false;
            if (Bool(ai, "GodModeEnabled")) return false;
            if (Bool(ai, "_isSafeSettlement")) return false;
            if (Bool(ai, "IsTalkActive")) return false;
            if (!Hurtable(ai)) return false;
            return FactionOf(ai) != null;
        }

        /// <summary>Would NPC_AI2.ApplyDamage take health off this NPC? It
        /// returns early for an NPC that is not initialized or whose settlement
        /// is a safe one, and a StoreKeeper never reaches DecreaseHealth
        /// (CONFIRMED IL, 2026-09-14): the settlement trader swallowed every
        /// round of the 6.16.5 squad. A guard that cannot be read refuses
        /// nothing.</summary>
        static bool Hurtable(Component ai)
        {
            try
            {
                if (_fInitialized != null && !(bool)_fInitialized.GetValue(ai)) return false;
                if (IntField(ai, _fBehavior, -1) == BehaviorStoreKeeper) return false;
                if (_fMySettlement != null && _fSafeSettlement != null)
                {
                    object home = _fMySettlement.GetValue(ai);
                    if (home != null && (bool)_fSafeSettlement.GetValue(home)) return false;
                }
            }
            catch { }
            return true;
        }

        static bool IsMine(Component ai)
        {
            if (ai == null) return false;
            if (_mPhotonView == null) return true;
            try
            {
                object view = _mPhotonView.Invoke(ai, null);
                if (view == null) return false;
                if (_mIsMine == null)
                    _mIsMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
                if (_mIsMine == null) return true;
                object r = _mIsMine.Invoke(view, null);
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        static Fighter FighterOf(Component ai)
        {
            for (int q = 0; q < _squads.Count; q++)
                for (int i = 0; i < _squads[q].Men.Count; i++)
                    if (_squads[q].Men[i].Ai == ai) return _squads[q].Men[i];
            for (int i = 0; i < _defenders.Count; i++)
                if (_defenders[i].Ai == ai) return _defenders[i];
            return null;
        }

        static readonly Dictionary<string, FieldInfo> _boolFields = new Dictionary<string, FieldInfo>();

        static bool Bool(Component c, string field)
        {
            if (c == null) return false;
            FieldInfo fi;
            if (!_boolFields.TryGetValue(field, out fi))
            {
                fi = AccessTools.Field(c.GetType(), field);
                if (fi != null && fi.FieldType != typeof(bool)) fi = null;
                _boolFields[field] = fi;
            }
            if (fi == null) return false;
            try { return (bool)fi.GetValue(c); }
            catch { return false; }
        }

        static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        static Vector3 FlatV(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        // -------------------------------------------------------------- status

        static string DebugTail()
        {
            if (CfgDebug == null || !CfgDebug.Value || _squads.Count == 0) return "";
            Squad s = _squads[0];
            int fire = 0, move = 0, hold = 0, armed = 0;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Ai == null || !Alive(f.Ai)) continue;
                if (f.Armed) armed++;
                if (f.Stance == Stance.Fire) fire++;
                else if (f.Stance == Stance.Hold) hold++;
                else move++;
            }
            return " | " + s.Tag + " " + s.Phase + ": " + armed + " armed, " + fire + " firing, "
                + move + " moving, " + hold + " holding"
                + (s.Threat == null ? ", no contact" : ", contact team " + s.BoundTeam + " bounds");
        }

        public static void Draw()
        {
            if (CfgDebug == null || !CfgDebug.Value || _status.Length == 0) return;
            GUI.Label(new Rect(8f, 8f, 720f, 22f), _status);
        }
    }
}
