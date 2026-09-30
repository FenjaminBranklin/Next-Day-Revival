"""Compile production bounty/audio code and direct-hit glue under C# 3.0.

Unity audio is simulated; this proves state transitions, not audible levels.
No game, Photon connection, TEMP directory, or installation is used.
"""
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def source(name):
    return (ROOT / name).read_text(encoding="utf-8")


def block(text, anchor):
    start = text.index(anchor)
    opening = text.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


HARNESS = r'''
using System;
using UnityEngine;
namespace UnityEngine {
 public struct Vector3 { }
 public static class Mathf { public static int Max(int a,int b) { return Math.Max(a,b); }
  public static float Clamp01(float a) { return Math.Max(0f,Math.Min(1f,a)); } }
 public enum AudioRolloffMode { Logarithmic }
 public class AudioSource {
  public bool loop, playOnAwake=true, mute, enabled=true, isPlaying;
  public float spatialBlend, minDistance, maxDistance, dopplerLevel, volume, pitch;
  public object clip; public AudioRolloffMode rolloffMode;
  public void Stop() { isPlaying=false; }
  public void Play() { if(enabled) isPlaying=true; }
 }
 public class GameObject {
  public AudioSource[] Sources = new AudioSource[0];
  internal NextDayRevival.An2Visual Plane; public int Scans;
  public GameObject gameObject { get { return this; } }
  public T GetComponent<T>() where T:class { return Plane as T; }
  public T AddComponent<T>() where T:new() { return new T(); }
  public T[] GetComponentsInChildren<T>(bool inactive) {
   if(!inactive) throw new Exception("inactive native engines must be included");
   Scans++; return Sources as T[];
  }
 }
}
namespace NextDayRevival {
 static class Mercs { internal static int LocalActor=41; }
 static class PlayerAn2 { internal static float K=2.8f; }
 static class AirKills { internal static int HitCredit=-1; @CREDIT@ }
 internal sealed class An2Visual {
  internal bool Running=true; bool _dead, _engineDead; float _power=1f;
  internal AudioSource _audio; internal GameObject _root=new GameObject();
  static object Clip() { return new object(); }
  @STOPENGINE@
  @SOUND@
  internal void TryRestart() { Running=true; Sound(1f); }
  internal bool EngineDead { get { return _engineDead && _power==0f; } }
 }
 static class GepardFx { internal static void Impact(Vector3 p,Vector3 n) { } }
 static class GepardGun {
  internal static int Paid; internal static bool Throw;
  internal static void Struck(object contacts,object owner,bool npc,GameObject go,Vector3 p,Vector3 d,int hits) {
   Paid=AirKills.Credit(); if(Throw) throw new Exception("simulated hit failure"); }
  internal static void Struck(GameObject go,Vector3 p,Vector3 d) { Paid=AirKills.Credit(); }
 }
 class Round { internal int Credit; internal bool Live=true; internal object Npc,Owner; }
 class Spec { internal int HeliHits; }
 class Collider { internal GameObject gameObject=new GameObject(); }
 class Hit { internal Collider collider=new Collider(); internal Vector3 point,normal; }
 static class Test {
  static int failures, checks;
  static void Ok(bool condition,string label) { checks++; Console.WriteLine((condition?"PASS ":"FAIL ")+label); if(!condition) failures++; }
  @ROUNDCREDIT@
  static bool Direct(Round r,Spec spec) {
   bool struck=true; Hit hit=new Hit(); Vector3 dir=new Vector3();
   @DIRECT@
   return false;
  }
  static void Attribution() {
   foreach(bool npc in new bool[]{false,true}) {
    foreach(int assigned in new int[]{-1,0,7,19}) {
     int expected=assigned>=0?assigned:npc?0:41;
     int credit=AirKillCore.ShotCredit(assigned,npc,41);
     Round r=new Round(); r.Credit=credit; if(npc) r.Npc=new object();
     Mercs.LocalActor=99; AirKills.HitCredit=13;
     Direct(r,npc?new Spec():null);
     Ok(credit==expected && GepardGun.Paid==expected && AirKills.HitCredit==13,
       "direct hit: NPC="+npc+" assigned="+assigned+" recipient="+GepardGun.Paid+"; launch owner survives local actor change");
     Mercs.LocalActor=41;
    }
   }
   Round zero=new Round(); zero.Credit=0; zero.Npc=new object();
   AirKills.HitCredit=22; GepardGun.Throw=true;
   try { Direct(zero,new Spec()); } catch { }
   Ok(AirKills.HitCredit==22,"exception restores enclosing credit"); GepardGun.Throw=false;
   foreach(int finalOwner in new int[]{0,7,19}) {
    DamageLedger ledger=new DamageLedger(); ledger.Add(0.95f,41); ledger.Add(0.05f,finalOwner);
    Ok(ledger.Down && ledger.Winner()==finalOwner,"95% player assist, final owner="+finalOwner);
    Ok(!ledger.Add(1f,99) && ledger.Winner()==finalOwner,"wreck cannot pay a second owner");
   }
   Ok(AirKillCore.ShotCredit(-1,true,41)==0,"town/garrison never inherits the master actor");
  }
  static void Audio() {
   AircraftAudio.StopEngines(null);
   foreach(string type in new string[]{"troop Mi-8","player Mi-8","player An-2","NPC An-2","Tu-95"}) {
    GameObject go=new GameObject();
    AudioSource native=new AudioSource(); native.loop=true; native.Play();
    AudioSource custom=new AudioSource(); custom.loop=true; custom.Play();
    AudioSource inactive=new AudioSource(); inactive.loop=true;
    AudioSource bang=new AudioSource(); bang.Play();
    go.Sources=new AudioSource[]{native,custom,inactive,bang};
    go.Plane=new An2Visual(); go.Plane._audio=custom;
    AircraftAudio.StopEngines(go);
    native.Play(); custom.Play(); inactive.Play(); go.Plane.TryRestart();
    Ok(!native.isPlaying && !custom.isPlaying && !inactive.isPlaying && !native.enabled && !native.playOnAwake && native.mute,
      type+": all propulsion loops stopped; late native Play cannot restart");
    Ok(bang.isPlaying && bang.enabled && !bang.mute,type+": one-shot explosion remains audible");
    Ok(go.Plane.EngineDead,type+": fixed-wing engine permanently off");
    AircraftAudio.StopEngines(go);
    Ok(bang.isPlaying,type+": repeated destruction is safe");
   }
   An2Visual early=new An2Visual(); early.StopEngine(); early.TryRestart();
   Ok(early._audio==null,"shot down before sound creation: no late engine source allocation");
  }
  static int Main() { Attribution(); Audio(); Console.WriteLine("runtime checks: "+checks+", failures: "+failures); return failures==0?0:1; }
 }
}
'''


def main():
    gepard = source("RevivalGepard.cs")
    an2 = source("Revival.PlayerAn2.cs")
    kills = source("Revival.AirKills.cs")
    helper = source("Revival.AircraftAudio.cs")
    harness = HARNESS
    for placeholder, snippet in {
        "CREDIT": block(kills, "internal static int Credit()"),
        "STOPENGINE": block(an2, "internal void StopEngine()"),
        "SOUND": block(an2, "void Sound(float rpm)"),
        "ROUNDCREDIT": block(gepard, "static int RoundCredit(Round r)"),
        "DIRECT": block(gepard[gepard.index("static bool Step("):], "if (struck)"),
    }.items():
        harness = harness.replace("@" + placeholder + "@", snippet)
    work = ROOT / "build" / "bounty_rotor_check"
    work.mkdir(parents=True, exist_ok=True)
    cs = work / "Harness.cs"
    cs.write_text(harness, encoding="ascii")
    exe = work / "check.exe"
    compiler = Path(os.environ.get("WINDIR", "C:/Windows")) / "Microsoft.NET/Framework64/v3.5/csc.exe"
    compiled = subprocess.run([str(compiler), "/nologo", "/warn:0", "/out:" + str(exe),
                               str(ROOT / "Revival.AirKillCore.cs"), str(ROOT / "Revival.AircraftAudio.cs"), str(cs)],
                              capture_output=True)
    if compiled.returncode:
        print(compiled.stdout.decode("utf-8", "replace"))
        return 1
    run = subprocess.run([str(exe)], capture_output=True)
    print(run.stdout.decode("utf-8", "replace").rstrip())
    checks = [
        (gepard.count("r.Credit = AirKillCore.ShotCredit(") == 2, "both launch paths snapshot ownership"),
        (gepard.count("AirKills.HitCredit = RoundCredit(r);") == 4, "direct/proximity/timed/ground hits carry projectile ownership"),
        ("merc != null ? Mathf.Max(0, merc.Actor) : 0" in source("Revival.Flak.cs")
         and "merc != null ? Mathf.Max(0, merc.Actor) : 0" in source("Revival.ShortRange.cs"), "52-K/ZU-23 merc owner, garrison zero"),
        ("if (_dead || _engineDead) { _power = 0f; }" in an2, "dead fixed-wing engine never spools up"),
        ("AircraftAudio.StopEngines(go);" in block(an2, "static void ShotDownHere(")
         and "AircraftAudio.StopEngines(go);" in block(an2, "static void Abandon(")
         and "AircraftAudio.StopEngines(go);" in block(an2, "static void Burn("), "An-2/Tu-95 local and remote crash paths stop engines"),
        (all("AircraftAudio.StopEngines(go);" in block(source("Revival.PlayerHeli.cs"), method)
             for method in ["static void Abandon(", "static void Burn("]), "player Mi-8 fall and impact stop engines"),
        ("if (phase == 0 || phase == 1) AircraftAudio.StopEngines(go);" in kills,
         "troop Mi-8 down/impact stops engines on every peer"),
        ("sender == MercAA.MasterActor()" in kills and "sender != MercAA.MasterActor()" in kills,
         "master alone announces troop crashes and bounty payments"),
        ("HeliCrashSound.Play" in block(kills, "static void OnHeli(")
         and "HeliCrashSound.Play" in block(an2, "static void Burn(")
         and "HeliCrashSound.Play" in block(source("Revival.PlayerHeli.cs"), "static void Burn("),
         "all carrier aircraft retain explosion/crash sound at impact"),
        ("StopOwnHum();" in block(source("Revival.FpvDrone.cs"), "static void Land(")
         and "f.Src.Stop();" in block(source("Revival.FpvDrone.cs"), "static void Entferne("),
         "FPV own/remote destruction already stops rotor hum"),
        ("void Update(" not in helper and "void Tick(" not in helper,
         "audio discovery runs only at destruction; no new per-frame scan"),
    ]
    failed = 0
    for passed, label in checks:
        print(("PASS " if passed else "FAIL ") + label)
        failed += not passed
    print("source checks: %d, failures: %d" % (len(checks), failed))
    return int(bool(run.returncode or failed))


if __name__ == "__main__":
    raise SystemExit(main())
