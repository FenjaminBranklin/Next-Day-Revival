"""Offline Q1: execute production session/policy and native-hook adapters with doubles."""
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build' / 'repair-tap-check'
HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using HarmonyLib;
namespace UnityEngine {
 public enum KeyCode {R,F,G,W,A,S,D,UpArrow,DownArrow,LeftArrow,RightArrow}
 public class Component {public GameObject gameObject;public Transform transform {get{return gameObject.transform;}}}
 public class Transform {public Vector3 position;public Transform root {get{return this;}}}
 public class GameObject {public bool activeInHierarchy=true;public Transform transform=new Transform();public Component life;
  public Component GetComponentInChildren(Type t){return life;}public Component GetComponentInParent(Type t){return life;}}
 public struct Vector3 {public float x,y,z;public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public float sqrMagnitude {get{return x*x+y*y+z*z;}}}
 public static class Time {public static int frameCount;}
 public static class Input {public static HashSet<KeyCode> Down=new HashSet<KeyCode>(),Held=new HashSet<KeyCode>();
  public static bool GetKeyDown(KeyCode k){return Down.Contains(k);}public static bool GetKey(KeyCode k){return Held.Contains(k);}}
}
namespace HarmonyLib {
 public class HarmonyMethod {public HarmonyMethod(MethodInfo m){}}
 public class Harmony {public void Patch(MethodInfo m,object a,object b,object c,object d,object e){if(m==null)throw new Exception("missing hook");}}
 public class CodeInstruction {public OpCode opcode;public object operand;public CodeInstruction(OpCode c,object o){opcode=c;operand=o;}}
 public static class AccessTools {
  public static MethodInfo Method(Type t,string n,Type[] p,object g){return t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);}
  public static FieldInfo Field(Type t,string n){return t.GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);}}
}
public class PlayerLifeData {public float Health=100;}
public class PlayerLifeDataManager:Component {public PlayerLifeData _playerLifeData=new PlayerLifeData();}
public static class MyInputManager {public static bool RemappedMovement;public static bool Button(int id){return RemappedMovement;}public static bool ButtonDown(int id){return Input.GetKeyDown(KeyCode.F);}}
public class PlayerInteractingManager:Component {
 public int Cancelled;
 public void SearchGameplayItems(){}
 public void PlayerVehicleInteract(){}
 public void CancelGlobalInteracting(){Cancelled++;NextDayRevival.RepairTap.NativeCancelled(this);}
 public class Iterator {public int state=20;public PlayerInteractingManager Manager;public bool MoveNext(){return true;}}
}
namespace NextDayRevival {
 static class RevivalPlugin {public static Log L=new Log();public static Type TypeByName(string s){return Type.GetType(s);}}
 class Log {public void LogInfo(string s){}public void LogError(string s){throw new Exception(s);}}
 static class FastField {public static FieldInfo Find(Type t,string n){return AccessTools.Field(t,n);}public static float GetNumber(FieldInfo f,object o){return (float)f.GetValue(o);}public static int GetInt(FieldInfo f,object o){return (int)f.GetValue(o);}}
 static class MapTools {public static GameObject Player;public static GameObject LocalPlayer(){return Player;}}
 static class GameUi {public static bool WindowOpen;}
 static class NativeActionProgress {public static int Cancelled;public static void End(string s){Cancelled++;RepairTap.End(s);}}
 class Test {
  static int checks;
  static void Ok(bool value,string name){checks++;if(!value)throw new Exception(name);}
  static PlayerLifeDataManager Life;
  static void Reset(){RepairTap.End("aa-repair");RepairTap.End("convoy-repair");RepairTap.End("an2-repair");RepairTap.End("vanilla-repair");
   Time.frameCount+=10;Input.Down.Clear();Input.Held.Clear();GameUi.WindowOpen=false;MyInputManager.RemappedMovement=false;
   MapTools.Player=new GameObject();Life=new PlayerLifeDataManager();Life.gameObject=MapTools.Player;MapTools.Player.life=Life;}
  static void Start(string owner){Reset();Input.Down.Add(KeyCode.R);Ok(RepairTap.Begin(owner,KeyCode.F),owner+" begins");Ok(RepairTap.Check(),"starting press is not cancellation");
   Time.frameCount++;Input.Down.Clear();Ok(RepairTap.Check(),"release continues");}
  static void Sessions(){foreach(string owner in new string[]{"aa-repair","convoy-repair","an2-repair"}){
   Start(owner);for(int n=0;n<300;n++){Time.frameCount++;Ok(RepairTap.Check(),"automatic progress remains active");}
   Input.Down.Add(KeyCode.R);Ok(!RepairTap.Check(),"second R cancels");Ok(!RepairTap.CanStart,"cancel edge cannot restart");Time.frameCount++;Ok(RepairTap.CanStart,"next frame available");
   Start(owner);Input.Held.Add(KeyCode.W);Ok(!RepairTap.Check(),"movement intention cancels even while frozen");
   Start(owner);MyInputManager.RemappedMovement=true;Ok(!RepairTap.Check(),"remapped movement cancels");
   Start(owner);MapTools.Player.transform.position=new Vector3(3,0,0);Ok(!RepairTap.Check(),"moving away cancels");
   Start(owner);Life._playerLifeData.Health=99;Ok(!RepairTap.Check(),"damage cancels before completion");
   Start(owner);float before;RepairTap.DamageBefore(Life,out before);Life._playerLifeData.Health=99;RepairTap.DamageAfter(before);
   Life._playerLifeData.Health=100;Ok(RepairTap.CanStart==false,"hit hook cancels before same-frame regeneration");
   Start(owner);RepairTap.DamageBefore(Life,out before);RepairTap.DamageAfter(before);Ok(RepairTap.Check(),"rejected hit does not cancel");
   Start(owner);Life._playerLifeData.Health=101;Ok(RepairTap.Check(),"healing is allowed");Life._playerLifeData.Health=100;Ok(!RepairTap.Check(),"damage after healing cancels");
   Start(owner);GameUi.WindowOpen=true;Ok(!RepairTap.Check(),"UI cancels");
   Start(owner);MapTools.Player=new GameObject();Ok(!RepairTap.Check(),"player replacement cancels");
   Start(owner);Input.Down.Add(KeyCode.F);Ok(!RepairTap.Check(),"configured key also cancels");
   Start(owner);RepairTap.End(owner);Ok(RepairTap.CanStart,"completion releases session");
  }}
  static object Iterator(PlayerInteractingManager manager,int state){PlayerInteractingManager.Iterator i=new PlayerInteractingManager.Iterator();i.Manager=manager;i.state=state;return i;}
  static void Native(){Reset();PlayerInteractingManager manager=new PlayerInteractingManager();manager.gameObject=MapTools.Player;
   // Fixture fields stand in for the shipped compiler-generated $this/state fields.
   typeof(RepairTap).GetField("_iteratorState",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,typeof(PlayerInteractingManager.Iterator).GetField("state"));
   typeof(RepairTap).GetField("_iteratorManager",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,typeof(PlayerInteractingManager.Iterator).GetField("Manager"));
   typeof(RepairTap).GetField("_cancel",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,typeof(PlayerInteractingManager).GetMethod("CancelGlobalInteracting"));
   object iterator=Iterator(manager,20);bool result=true;
   Ok(RepairTap.NativeBefore(iterator,ref result),"native repair begins");Time.frameCount++;Life._playerLifeData.Health=90;
   Ok(!RepairTap.NativeBefore(iterator,ref result)&&!result&&manager.Cancelled==1,"native damage blocks inventory/RPC completion");
   Reset();manager.gameObject=MapTools.Player;iterator=Iterator(manager,22);result=true;
   Ok(RepairTap.NativeBefore(iterator,ref result)&&RepairTap.CanStart,"native fuel untouched");
   iterator=Iterator(manager,20);Ok(RepairTap.NativeBefore(iterator,ref result),"new native repair");
   RepairTap.NativeAfter(iterator,false);Ok(RepairTap.CanStart,"native completion releases guard");
   iterator=Iterator(manager,20);Ok(RepairTap.NativeBefore(iterator,ref result),"native restart");
   manager.CancelGlobalInteracting();Ok(RepairTap.CanStart,"engine/range/native cancel clears guard");
  }
  static void Transpiler(){
   MethodInfo button=typeof(MyInputManager).GetMethod("ButtonDown"),repair=typeof(PlayerInteractingManager).GetMethod("PlayerVehicleInteract");
   List<CodeInstruction> code=new List<CodeInstruction>();code.Add(new CodeInstruction(OpCodes.Call,button));
   code.Add(new CodeInstruction(OpCodes.Ldc_I4_S,(sbyte)20));code.Add(new CodeInstruction(OpCodes.Ldloc_0,null));code.Add(new CodeInstruction(OpCodes.Ldloc_1,null));code.Add(new CodeInstruction(OpCodes.Call,repair));
   code.Add(new CodeInstruction(OpCodes.Call,button));code.Add(new CodeInstruction(OpCodes.Ldc_I4_S,(sbyte)22));code.Add(new CodeInstruction(OpCodes.Ldloc_0,null));code.Add(new CodeInstruction(OpCodes.Ldloc_1,null));code.Add(new CodeInstruction(OpCodes.Call,repair));
   List<CodeInstruction> patched=new List<CodeInstruction>(RepairTap.RepairInput(code));
   Ok(((MethodInfo)patched[0].operand).Name=="NativePressed"&&patched[5].operand==button,"R alias patches repair only");
   bool refused=false;try{RepairTap.RepairInput(new CodeInstruction[0]);}catch(InvalidOperationException){refused=true;}Ok(refused,"unknown IL refused");
  }
  public static int Main(){
   typeof(RepairTap).GetField("_held",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,
    Delegate.CreateDelegate(typeof(RepairTap).GetField("_held",BindingFlags.Static|BindingFlags.NonPublic).FieldType,typeof(MyInputManager).GetMethod("Button")));
   typeof(RepairTap).GetField("_button",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,
    Delegate.CreateDelegate(typeof(RepairTap).GetField("_button",BindingFlags.Static|BindingFlags.NonPublic).FieldType,typeof(MyInputManager).GetMethod("ButtonDown")));
   Reset();Input.Down.Add(KeyCode.R);Ok(RepairTap.NativePressed(17),"native R alias");Input.Down.Clear();Input.Down.Add(KeyCode.F);Ok(RepairTap.NativePressed(17),"native original key retained");
   Sessions();Native();Transpiler();Console.WriteLine("Q1 PASS: "+checks+" checks; tap/release, toggle, move, damage, UI, native completion/authority seams");return 0;}
 }
}
'''


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    harness = OUT / 'Harness.cs'
    harness.write_text(HARNESS, encoding='ascii')
    exe = OUT / 'check.exe'
    compiler = Path(r'C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe')
    result = subprocess.run([str(compiler), '/nologo', '/target:exe', '/out:' + str(exe),
                             str(ROOT / 'Revival.RepairTap.cs'), str(ROOT / 'Revival.RepairTapCore.cs'),
                             str(harness)], capture_output=True, text=True)
    if result.returncode:
        print(result.stdout + result.stderr)
        return result.returncode
    result = subprocess.run([str(exe)], capture_output=True, text=True)
    print(result.stdout + result.stderr, end='')
    if result.returncode:
        return result.returncode
    # Verify the measured native entry point, timer, consumption and RPC path.
    sys.path.insert(0, str(ROOT / 'research'))
    import ilq
    native = '\n'.join(ilq.asm().dis('PlayerInteractingManager::SearchGameplayItems'))
    timer = '\n'.join(ilq.asm().dis('<PlayerVehicleInteract>c__Iterator4::MoveNext'))
    assert 'MyInputManager::ButtonDown' in native
    assert not any('MyInputManager::Button' in line and 'ButtonDown' not in line for line in native.splitlines())
    lines = native.splitlines()
    repair_calls = [i for i, line in enumerate(lines) if 'PlayerVehicleInteract' in line
                    and 'ldc.i4.s' in lines[i - 3] and lines[i - 3].split()[-1] == '20']
    assert len(repair_calls) == 1
    assert any('MyInputManager::ButtonDown' in line for line in lines[repair_calls[0] - 50:repair_calls[0]])
    assert 'WaitForSeconds' in timer and 'ClearBackpackSlot' in timer and 'PhotonView::RPC' in timer
    assert not any(x in timer for x in ('Input::GetKey', 'MyInputManager::Button'))
    print('Q1 PASS: shipped native repair is tap + timed completion; existing item/RPC path retained')
    return 0


if __name__ == '__main__':
    sys.exit(main())
