"""A L2 flak engage-early: offline standard-raid simulation on the real gun numbers.

No game, network or installation. Compiles the unchanged production cores
(FlakEngageCore, MercAACore, RadarClarityCore, AirPicturePolicy, AirKillCore,
RetakeRaidsCore, FlakPositionsCore) with csc 3.5 and a harness that mirrors
FlakFire.Search / Control / Trigger and Flak.Slew / Intercept / Shoot. The
6.66.0 target rules (OLD) and the A L2 rules (NEW) run on identical raids.

Numbers read from source: 52-K muzzle velocity, rates of fire, reload, rounds,
traverse/elevation/acceleration, tracking lag, ceiling, pitch limits, burst
radius, hits to kill, retake-raid composition and delays, aircraft altitudes,
speeds and the NpcAircraft speed factor, Mi-8 approach.

Assumptions (stated in the output): level straight flight; timed bursts only
(no proximity-fuze bonus), 3.2 m aircraft radius; per-ray LOS loss probability
models terrain/tree samples at long range; garrison guns have unlimited
racks, merc guns are stocked. Unity colliders, the native airframe damage and
F6 cost are not simulated.
"""
from pathlib import Path
import os
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build' / 'flak_engage_early_sim'

HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Globalization;
using NextDayRevival;

struct V {
 public double x,y,z;
 public V(double a,double b,double c){x=a;y=b;z=c;}
 public static V operator+(V a,V b){return new V(a.x+b.x,a.y+b.y,a.z+b.z);}
 public static V operator-(V a,V b){return new V(a.x-b.x,a.y-b.y,a.z-b.z);}
 public static V operator*(V a,double k){return new V(a.x*k,a.y*k,a.z*k);}
 public double Length{get{return Math.Sqrt(x*x+y*y+z*z);}}
 public V Unit{get{double l=Length;return l<1e-9?new V(0,0,1):this*(1/l);}}
 public static double Dot(V a,V b){return a.x*b.x+a.y*b.y+a.z*b.z;}
 public static V Cross(V a,V b){return new V(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);}
 public static double Angle(V a,V b){double c=Dot(a.Unit,b.Unit);c=Math.Max(-1,Math.Min(1,c));return Math.Acos(c)*180/Math.PI;}
}

class Plane {
 public int Type;public V Start,Vel;public double Spawn,Deadline,Radius=9/2.8;
 public DamageLedger Ledger=new DamageLedger();public double KilledAt=-1,FirstShot=-1,FirstShotDist;
 public V Pos(double t){return Start+Vel*(t-Spawn);}
 public bool Up(double t){return t>=Spawn&&!Ledger.Down;}
}

class Gun {
 public int Index;public V At;public bool Crew,Merc,Both;public int Trait;
 public int Target=-1,Lost=-1;public bool Engaged;public double Held,LastContact=-100,NextLook,NextShot,ReloadUntil=-1;
 public int Rounds=@ROUNDS@,Fired;public V Err,LastVel;public double Yaw,Pitch=30,YawVel,PitchVel,LastWantYaw,LastWantPitch,WantYawRate,WantPitchRate,WantYaw,WantPitch=30;
 public double[] Seen;public V[] Est;public bool[] EstInit;
}

class Shell {public V Burst;public double At;}

static class Sim {
 const double G=9.81,Speed=@VELOCITY@,Turn=@TURN@,Elev=@ELEV@,Acc=@ACCEL@,PitchMin=@PITCHMIN@,PitchMax=@PITCHMAX@;
 const double Ceiling=@CEILING@,Lag=@LAG@,Splash=@SPLASH@,MaxFuze=@MAXFUZE@,Reload=@RELOAD@,SingleRpm=@RPM@,CrewRpm=@CREWRPM@;
 const double KmhFactor=@SPEEDFACTOR@;
 static readonly V Tower=new V(4272.12/2.8,0,1335.05/2.8),RadarHead=new V(4262.0/2.8,0,1392.0/2.8);
 static readonly V Fuel=new V(4090/2.8,0,550/2.8),Lz=new V(4632/2.8,0,1150/2.8);
 public static double LosLoss;public static bool New,RadarManned;
 static System.Random R;
 static double U(double a,double b){return a+(b-a)*R.NextDouble();}
 static V Sphere(){while(true){V v=new V(U(-1,1),U(-1,1),U(-1,1));double l=v.Length;if(l<=1&&l>1e-6)return v;}}
 static V OnSphere(){return Sphere().Unit;}

 static AACalibration Cal(Gun g){return MercAACore.Calibrate(g.Merc,RadarManned,g.Merc?g.Trait:0);}
 static bool InZone(V p){return FlakEngageCore.InZone((float)(p.x*2.8),(float)(p.z*2.8),FlakEngageCore.ZoneMetres,2.8f);}
 static double Reach(Gun g,V p,AACalibration c){
  if(!New)return c.Range;
  return FlakEngageCore.Reach(c.Range,InZone(p),FlakEngageCore.ZoneMetres,(float)MaxFuze);
 }
 static int Threat(Plane a,V p,V v){
  V d=(p-RadarHead)*2.8;int eta=RadarClarityCore.Eta((float)d.x,(float)d.z,(float)(v.x*2.8),(float)(v.z*2.8));
  int kind=a.Type==3?0:6;int type=AirPicturePolicy.Type(kind,a.Type==0);
  int range=(int)Math.Round(Math.Sqrt(d.x*d.x+d.z*d.z)/2.8/100);
  return RadarClarityCore.Threat(-1,type,eta,range);
 }
 static bool Sight(){return R.NextDouble()>=LosLoss;}
 static bool Airborne(V p){return p.y>3;}
 static void Angles(V d,out double yaw,out double pitch){
  yaw=Math.Atan2(d.x,d.z)*180/Math.PI;pitch=Math.Atan2(d.y,Math.Sqrt(d.x*d.x+d.z*d.z))*180/Math.PI;}
 static V Dir(double yaw,double pitch){double y=yaw*Math.PI/180,p=pitch*Math.PI/180;return new V(Math.Sin(y)*Math.Cos(p),Math.Sin(p),Math.Cos(y)*Math.Cos(p));}
 static double DeltaAngle(double a,double b){double d=(b-a)%360;if(d>180)d-=360;if(d<-180)d+=360;return d;}
 static double Clamp(double v,double a,double b){return v<a?a:v>b?b:v;}
 static double MoveTowards(double c,double t,double m){return Math.Abs(t-c)<=m?t:c+Math.Sign(t-c)*m;}

 // Flak.Slew: feed-forward of the laying point's rate, braking law, handwheel limits.
 static void Slew(Gun g,double dt){
  double k=1-Math.Exp(-dt/0.3);
  double ry=Clamp(DeltaAngle(g.LastWantYaw,g.WantYaw)/dt,-Turn,Turn),rp=Clamp((g.WantPitch-g.LastWantPitch)/dt,-Elev,Elev);
  if(Math.Abs(DeltaAngle(g.LastWantYaw,g.WantYaw))>20)ry=0;if(Math.Abs(g.WantPitch-g.LastWantPitch)>20)rp=0;
  g.WantYawRate+=(ry-g.WantYawRate)*k;g.WantPitchRate+=(rp-g.WantPitchRate)*k;g.LastWantYaw=g.WantYaw;g.LastWantPitch=g.WantPitch;
  double dy=DeltaAngle(g.Yaw,g.WantYaw),stop=Math.Sqrt(2*Acc*Math.Abs(dy));
  g.YawVel=MoveTowards(g.YawVel,Clamp(g.WantYawRate+Clamp(dy*4,-stop,stop),-Turn,Turn),Acc*dt);g.Yaw+=g.YawVel*dt;
  if(g.Yaw>180)g.Yaw-=360;if(g.Yaw<-180)g.Yaw+=360;
  double wp=Clamp(g.WantPitch,PitchMin,PitchMax),dp=wp-g.Pitch,stopP=Math.Sqrt(2*Acc*Math.Abs(dp));
  double ff=(wp<=PitchMin&&g.WantPitchRate<0)||(wp>=PitchMax&&g.WantPitchRate>0)?0:g.WantPitchRate;
  g.PitchVel=MoveTowards(g.PitchVel,Clamp(ff+Clamp(dp*4,-stopP,stopP),-Elev,Elev),Acc*dt);
  g.Pitch=Clamp(g.Pitch+g.PitchVel*dt,PitchMin,PitchMax);
 }

 // FlakFire.Search with the 6.66.0 (OLD) or A L2 (NEW) range/stay/switch rules.
 static void Search(Gun g,Gun[] guns,List<Plane> air,double now){
  AACalibration cal=Cal(g);bool direction=RadarManned;
  double collect=New?FlakEngageCore.CollectMetres(cal.Range,true,FlakEngageCore.ZoneMetres,(float)MaxFuze):cal.Range;
  int best=-1,bestThreat=int.MinValue;double bestD=double.MaxValue;
  for(int i=0;i<air.Count;i++){
   Plane a=air[i];if(!a.Up(now))continue;V p=a.Pos(now);double d=(p-g.At).Length;
   if(d*d>collect*collect*1.3)continue;
   if(g.Seen[i]<0||now-g.Seen[i]>1.2){g.EstInit[i]=false;}
   g.Seen[i]=now;
   if(!Airborne(p))continue;
   if(d>Reach(g,p,cal)||p.y-g.At.y>Ceiling)continue;
   int cover=0;for(int o=0;o<guns.Length;o++)if(guns[o]!=g&&guns[o].Crew&&guns[o].Target==i)cover++;
   double score=direction?MercAACore.TargetScore((float)d,cover):d;
   int threat=direction?Threat(a,p,g.Est[i]):0;
   if(threat<bestThreat||(threat==bestThreat&&score>=bestD))continue;
   if(!Sight())continue;
   best=i;bestD=score;bestThreat=threat;
  }
  int cur=g.Target;
  if(cur>=0&&best>=0&&best!=cur&&air[cur].Up(now)){
   V p=air[cur].Pos(now);double d=(p-g.At).Length;
   bool range=d<=(New?Reach(g,p,cal):cal.Range)&&p.y-g.At.y<=Ceiling;
   int ct=direction?Threat(air[cur],p,g.Est[cur]):0;
   bool keep=New?FlakEngageCore.Keep(ct,bestThreat,direction):(!direction||ct>=bestThreat);
   if(range&&keep&&Sight())best=cur;
  }
  if(best!=cur){
   if(New){
    if(best<0)g.Lost=cur;
    else if(!FlakEngageCore.Resume(g.Engaged,cur<0&&best==g.Lost,(float)(now-g.LastContact))){g.Held=0;g.Engaged=false;}
   }else{g.Held=0;g.Engaged=false;}
  }
  g.Target=best;
 }

 static V Offset(V los,double size){V e=OnSphere();e=e-los*V.Dot(e,los);if(e.Length<1e-3)e=new V(1,0,0);return e.Unit*size*U(.85,1.15);}
 static V Scatter(V los,double size){V e=Sphere();e=e-los*(V.Dot(e,los)*.7);return e*size;}

 static void Control(Gun g,Gun[] guns,List<Plane> air,List<Shell> flight,double now,double dt){
  if(now>=g.NextLook){g.NextLook=now+.5;Search(g,guns,air,now);}
  double rate=Lag*(RadarManned?2:1);double kf=1-Math.Exp(-dt*rate);
  for(int i=0;i<air.Count;i++)if(g.Seen[i]>=0&&now-g.Seen[i]<=1.2&&air[i].Up(now)){
   if(!g.EstInit[i]){g.Est[i]=new V(0,0,0);g.EstInit[i]=true;}else g.Est[i]=g.Est[i]+(air[i].Vel-g.Est[i])*kf;}
  if(g.ReloadUntil>=0&&now>=g.ReloadUntil){g.ReloadUntil=-1;g.Rounds=@ROUNDS@;}
  int ti=g.Target;
  if(ti<0||!air[ti].Up(now)){
   if(ti>=0&&!air[ti].Up(now))g.Target=-1;
   if(g.Engaged&&now-g.LastContact>(New?FlakEngageCore.ResumeSeconds:4))g.Engaged=false;
   Slew(g,dt);return;
  }
  Plane t=air[ti];V p=t.Pos(now),mid=g.At,rel=p-mid;double dist=rel.Length;g.LastContact=now;
  AACalibration c=MercAA_Calibration(g);
  if(!g.Engaged){g.Engaged=true;g.Fired=0;g.Held=0;g.Err=Offset(rel.Unit,dist*c.InitialMil*.001);g.LastVel=g.Est[ti];}
  V v=g.Est[ti];
  double tof=MercAACore.FlightTime((float)rel.x,(float)rel.y,(float)rel.z,(float)v.x,(float)v.y,(float)v.z,(float)Speed,(float)G);
  V aim=p+v*tof+new V(0,.5*G*tof*tof,0)+g.Err;double wy,wpi;Angles(aim-mid,out wy,out wpi);
  bool reach=wpi>=PitchMin-.5&&wpi<=PitchMax+.5;g.WantYaw=wy;g.WantPitch=Clamp(wpi,PitchMin,PitchMax);
  Slew(g,dt);
  V bore=Dir(g.Yaw,g.Pitch);double error=V.Angle(bore,aim-mid);g.Held+=dt;
  bool ready=reach&&g.Held>=c.Reaction&&error<1.5&&g.ReloadUntil<0;
  if(!ready||now<g.NextShot)return;
  if(g.Rounds<=0){g.ReloadUntil=now+Reload*MercAACore.ReloadScale(g.Merc,g.Both);return;}
  double dv=(v-g.LastVel).Length*2.8;g.LastVel=v;
  if(g.Fired>0&&dv>.5)g.Err=g.Err+OnSphere()*((dv/2.8)*tof);
  // Flak.Shoot: the bore unless within 1 deg of the want, 2 mil dispersion, calibrated fuze error.
  V want=(aim-mid).Unit,dir=V.Angle(bore,want)<1?want:bore;
  V right=V.Cross(new V(0,1,0),dir).Unit,upv=V.Cross(dir,right);
  double sr=Math.Sqrt(R.NextDouble())*@DISPERSION@*.001,sa=U(0,2*Math.PI);
  dir=(dir+right*(sr*Math.Cos(sa))+upv*(sr*Math.Sin(sa))).Unit;
  double range=Clamp((aim-mid).Length,30/2.8*2.8,MaxFuze),life=range/Speed*U(1-c.FuzeError,1+c.FuzeError);
  Shell s=new Shell();s.At=now+life;s.Burst=mid+dir*(Speed*life)+new V(0,-.5*G*life*life,0);flight.Add(s);
  if(t.FirstShot<0){t.FirstShot=now;t.FirstShotDist=dist;}
  Shots++;g.Rounds--;g.Fired++;
  g.NextShot=MercAACore_NextShot(g.NextShot,now,dt,MercAACore.CrewInterval((float)(60/SingleRpm),(float)(60/CrewRpm),g.Merc,RadarManned,g.Both)*U(.92,1.08));
  g.Err=g.Err*c.Walk+Scatter(rel.Unit,c.FloorMil*.001*dist);
  if(g.Rounds<=0)g.ReloadUntil=now+Reload*MercAACore.ReloadScale(g.Merc,g.Both);
 }
 static double MercAACore_NextShot(double due,double now,double dt,double interval){return (now-due<=Math.Max(0,dt)?due:now)+interval;}
 // MercAA.Calibration with the shipped config knobs (all at their defaults: scale 1).
 static AACalibration MercAA_Calibration(Gun g){return Cal(g);}

 public static int Shots;
 static Plane Make(int type,V target,V source,double spawn,double approachM,double altM,double kmh,double releaseM){
  Plane a=new Plane();a.Type=type;a.Spawn=spawn;double speed=kmh*KmhFactor/3.6;
  a.Start=target+source*approachM+new V(0,altM,0);a.Vel=source*(-speed);
  a.Deadline=spawn+Math.Max(0,(approachM-releaseM)/speed);return a;
 }
 public static int[] Kills=new int[4],Count=new int[4];public static int Paras,ParaCount;
 public static List<double> Lead=new List<double>(),FirstDist=new List<double>(),BomberLead=new List<double>(),TransportLead=new List<double>();public static int Unshot;
 // Gun crews: mode 0 none, 1 garrison two-seat, 2 one normal merc, 3 two trait mercs, 4 two trait + one normal
 public static void Raid(int seed,int mode){
  R=new System.Random(seed);double heading=U(0,2*Math.PI);
  V source=new V(Math.Sin(heading),0,Math.Cos(heading)),across=new V(source.z,0,-source.x);
  RetakeRaid plan=RetakePlan.Compose(new RetakeSettings(),0,0,false,new int[3]);
  Gun[] guns=new Gun[3];List<Plane> air=new List<Plane>();
  V[] targets={Tower,new V(FlakPositionsCore.X[1]/2.8,0,FlakPositionsCore.Z[1]/2.8),Fuel};
  V bombT=targets[seed%3];
  double approach=(MercAACore.ApproachUnits(0f,2.8f))/2.8;
  for(int i=0;i<plan.Escorts;i++){V gt=new V(FlakPositionsCore.X[i%3]/2.8,0,FlakPositionsCore.Z[i%3]/2.8);
   double alt=@ESCORT_ALT@+U(-10,10);air.Add(Make(1,gt,source,0,approach+@ESCORT_STICK@/2/2.8,alt,@ESCORT_KMH@,@ESCORT_STICK@/2/2.8+@ESCORT_KMH@*KmhFactor/3.6*Math.Sqrt(2*alt/G)));}
  for(int i=0;i<plan.Bombers;i++){double alt=@BOMBER_ALT@+U(-50,50);
   air.Add(Make(0,bombT+across*(i*80/2.8),source,@BOMBER_DELAY@,approach+@BOMBER_STICK@/2/2.8,alt,@BOMBER_KMH@,@BOMBER_STICK@/2/2.8+@BOMBER_KMH@*KmhFactor/3.6*Math.Sqrt(2*alt/G)));}
  for(int i=0;i<plan.Transports;i++){double alt=@TRANSPORT_ALT@+U(-10,10);
   air.Add(Make(2,Tower+across*((i-.5)*200/2.8),source,@TRANSPORT_DELAY@+i*3,approach+@JUMP_SPREAD@/2/2.8,alt,@TRANSPORT_KMH@,@JUMP_SPREAD@/2/2.8));}
  for(int i=0;i<plan.Helis;i++)air.Add(Make(3,Lz,source,@HELI_DELAY@+i*@HELI_STAGGER@,@HELI_APPROACH@,@HELI_ALT@,@HELI_KMH@,150));
  // The shipped second air wave: another bomber pass after the first stick's jump.
  if(new RetakeSettings().SecondWave){double alt=@BOMBER_ALT@+U(-50,50);double jump=air[air.Count-1-plan.Helis].Deadline;
   air.Add(Make(0,targets[(seed+1)%3],source,jump+new RetakeSettings().SecondWaveDelaySeconds,approach+@BOMBER_STICK@/2/2.8,alt,@BOMBER_KMH@,@BOMBER_STICK@/2/2.8+@BOMBER_KMH@*KmhFactor/3.6*Math.Sqrt(2*alt/G)));}
  for(int k=0;k<3;k++){Gun g=new Gun();g.Index=k;g.At=new V(FlakPositionsCore.X[k]/2.8,1.8,FlakPositionsCore.Z[k]/2.8);
   g.Seen=new double[air.Count];g.Est=new V[air.Count];g.EstInit=new bool[air.Count];for(int i=0;i<air.Count;i++)g.Seen[i]=-1;
   g.Crew=mode==1||(mode==2&&k==0)||(mode==3&&k<2)||mode==4;g.Merc=mode>=2;g.Both=mode==1;
   g.Trait=(mode==3||mode==4)&&k<2?25:0;g.Yaw=FlakPositionsCore.Heading(k)+U(-60,60);g.NextLook=k*.125;guns[k]=g;}
  List<Shell> flight=new List<Shell>();double end=0;foreach(Plane a in air)end=Math.Max(end,a.Deadline+5);
  const double dt=.05;
  for(double now=0;now<=end;now+=dt){
   for(int s=flight.Count-1;s>=0;s--)if(flight[s].At<=now){
    Shell sh=flight[s];foreach(Plane a in air)if(a.Up(sh.At)&&(a.Pos(sh.At)-sh.Burst).Length<=Splash+a.Radius){
     a.Ledger.Add(AirKillCore.GunHitDamage(@HELI_HITS@),1);if(a.Ledger.Down)a.KilledAt=sh.At;}
    flight.RemoveAt(s);}
   foreach(Gun g in guns)if(g.Crew)Control(g,guns,air,flight,now,dt);
  }
  foreach(Plane a in air){Count[a.Type]++;bool pre=a.Ledger.Down&&a.KilledAt<a.Deadline;if(pre)Kills[a.Type]++;
   if(a.Type==2){ParaCount+=plan.Paratroopers;if(!pre)Paras+=plan.Paratroopers;}
   if(mode>0){if(a.FirstShot>=0&&a.FirstShot<a.Deadline){Lead.Add(a.Deadline-a.FirstShot);FirstDist.Add(a.FirstShotDist);
    if(a.Type==0)BomberLead.Add(a.Deadline-a.FirstShot);if(a.Type==2)TransportLead.Add(a.Deadline-a.FirstShot);}else Unshot++;}}
 }
}

class Check {
 static string P(double v){return v.ToString("0.0",CultureInfo.InvariantCulture);}
 static double Median(List<double> l){if(l.Count==0)return double.NaN;l.Sort();return l[l.Count/2];}
 static double Pct(List<double> l,double q){if(l.Count==0)return double.NaN;l.Sort();return l[(int)Math.Min(l.Count-1,Math.Floor(l.Count*q))];}
 static double bomberLead;
 static double Run(string label,int mode,bool radar,bool fresh,double los,int n,out double lead){
  Sim.New=fresh;Sim.RadarManned=radar;Sim.LosLoss=los;Sim.Shots=0;Array.Clear(Sim.Kills,0,4);Array.Clear(Sim.Count,0,4);
  Sim.Paras=Sim.ParaCount=0;Sim.Lead.Clear();Sim.FirstDist.Clear();Sim.BomberLead.Clear();Sim.TransportLead.Clear();Sim.Unshot=0;
  for(int i=0;i<n;i++)Sim.Raid(20261002+i,mode);
  int k=0,c=0;for(int t=0;t<4;t++){k+=Sim.Kills[t];c+=Sim.Count[t];}
  double rate=100.0*k/c;lead=Median(Sim.Lead);
  Console.WriteLine(label.PadRight(44)+(fresh?" NEW ":" OLD ")+P(rate).PadLeft(6)+"%  Tu95 "+Sim.Kills[0]+"/"+Sim.Count[0]+"  esc "+Sim.Kills[1]+"/"+Sim.Count[1]
   +"  trn "+Sim.Kills[2]+"/"+Sim.Count[2]+"  Mi8 "+Sim.Kills[3]+"/"+Sim.Count[3]+"  paras alive "+Sim.Paras+"/"+Sim.ParaCount
   +(mode==0?"":"  first shot: median "+P(lead)+" s / "+P(Median(Sim.FirstDist))+" m before delivery, p10 "+P(Pct(Sim.Lead,.1))+" s, unshot "+Sim.Unshot)
   +"  shells "+Sim.Shots);
  if(mode>0)Console.WriteLine("".PadRight(49)+"first shot before delivery, median: Tu-95 "+P(Median(Sim.BomberLead))+" s (p10 "+P(Pct(Sim.BomberLead,.1))+" s), transport "+P(Median(Sim.TransportLead))+" s (p10 "+P(Pct(Sim.TransportLead,.1))+" s)");
  bomberLead=Median(Sim.BomberLead);
  return rate;
 }
 public static int Main(){
  int fails=0;const int n=@RAIDS@;double lead;
  // Core rules: the zone, the reach, the hysteresis and the resume window.
  if(!FlakEngageCore.InZone(4272.12f,1335.05f,4500f,2.8f)||FlakEngageCore.InZone(4272.12f+4600*2.8f,1335.05f,4500f,2.8f)){Console.WriteLine("FAIL zone");fails++;}
  if(FlakEngageCore.Reach(1800f,true,4500f,8000f)!=5500f||FlakEngageCore.Reach(1800f,false,4500f,8000f)!=1800f||FlakEngageCore.Reach(7000f,true,4500f,8000f)!=7000f){Console.WriteLine("FAIL reach");fails++;}
  if(!FlakEngageCore.Keep(4000,4149,true)||FlakEngageCore.Keep(4000,4150,true)||!FlakEngageCore.Keep(0,9999,false)){Console.WriteLine("FAIL keep");fails++;}
  if(!FlakEngageCore.Resume(true,true,3.9f)||FlakEngageCore.Resume(true,true,4.1f)||FlakEngageCore.Resume(false,true,1f)||FlakEngageCore.Resume(true,false,1f)){Console.WriteLine("FAIL resume");fails++;}
  Console.WriteLine("core rules: zone/reach/keep/resume "+(fails==0?"PASS":"FAIL"));
  RetakeRaid plan=RetakePlan.Compose(new RetakeSettings(),0,0,false,new int[3]);
  Console.WriteLine("Standard raid = shipped level 0: "+plan.Bombers+" Tu-95 + second-wave Tu-95, "+plan.Escorts+" escort An-2, "+plan.Transports+" transport An-2 x "+plan.Paratroopers+", "+plan.Helis+" Mi-8; "+n+" raids per row, random heading, bomb target rotates tower/guns/fuel.");
  Console.WriteLine("Speeds x @SPEEDFACTOR@ (NpcAircraft.SpeedFactor): Tu-95 "+P(@BOMBER_KMH@*@SPEEDFACTOR@)+" km/h at @BOMBER_ALT@ m, An-2 escort "+P(@ESCORT_KMH@*@SPEEDFACTOR@)+" km/h at @ESCORT_ALT@ m, transport "+P(@TRANSPORT_KMH@*@SPEEDFACTOR@)+" km/h at @TRANSPORT_ALT@ m.");
  Console.WriteLine("52-K: @VELOCITY@ m/s, crew @CREWRPM@ rpm / one man @RPM@ rpm, @ROUNDS@ rounds + @RELOAD@ s reload, traverse @TURN@ deg/s, elevation @ELEV@ deg/s; airfield zone "+FlakEngageCore.ZoneMetres+" m around C1.");
  Console.WriteLine("Kill = brought down before its delivery point (bomb release, first jump, Mi-8 descent).");
  foreach(double los in new double[]{0,@LOS@}){
   Console.WriteLine();Console.WriteLine("LOS loss per ray: "+P(los*100)+" %");
   Run("unmanned (no crew)",0,false,true,los,n,out lead);
   Run("garrison crews, radar unmanned (Kevin 6.66)",1,false,false,los,n,out lead);
   double garrison=Run("garrison crews, radar unmanned (Kevin 6.66)",1,false,true,los,n,out lead);
   if(lead<20||bomberLead<30){Console.WriteLine("FAIL garrison first shot not well before delivery");fails++;}
   Run("garrison crews, radar manned",1,true,false,los,n,out lead);
   double directed=Run("garrison crews, radar manned",1,true,true,los,n,out lead);
   if(!(directed>garrison+10)){Console.WriteLine("FAIL the manned radar adds too little");fails++;}
   Run("partly: one normal merc 52-K, no radar",2,false,false,los,n,out lead);
   Run("partly: one normal merc 52-K, no radar",2,false,true,los,n,out lead);
   Run("partly: two trait mercs, no radar",3,false,true,los,n,out lead);
   Run("full: 2 trait + 1 normal merc + radar",4,true,false,los,n,out lead);
   double full=Run("full: 2 trait + 1 normal merc + radar",4,true,true,los,n,out lead);
   if(lead<30||bomberLead<30){Console.WriteLine("FAIL full first shot not well before delivery");fails++;}
   if(full<95){Console.WriteLine("FAIL full defence below 95 %");fails++;}
   Run("full mercs, radar unmanned",4,false,true,los,n,out lead);
  }
  Console.WriteLine();Console.WriteLine(fails==0?"RESULT: PASS":"RESULT: FAIL ("+fails+")");
  return fails==0?0:1;
 }
}
'''


def number(text, name):
    m = re.search(r'\b' + re.escape(name) + r'\s*=\s*(-?[\d.]+)f?\b', text)
    if not m:
        raise SystemExit('constant not found: ' + name)
    return m.group(1)


def bind(text, key):
    m = re.search(r'"' + re.escape(key) + r'",\s*(-?[\d.]+)f?', text)
    if not m:
        raise SystemExit('config not found: ' + key)
    return m.group(1)


def main():
    read = lambda name: (ROOT / name).read_text(encoding='utf-8-sig')
    flak, events, retake, npc, troops, ammo = map(read, (
        'Revival.Flak.cs', 'Revival.AirEvents.cs', 'Revival.RetakeRaids.cs', 'Revival.NpcAircraft.cs',
        'RevivalTroopInsertion.cs', 'Revival.FlakAmmo.cs'))
    # The runtime wiring this harness mirrors must still be in place.
    search = flak[flak.index('internal static void Search(Flak.Gun g)'):flak.index('static float Flak_Ceiling()')]
    for need in ('FlakEngageCore.CollectMetres(', 'Flak.ReachU(g, c.Pos,', 'FlakEngageCore.Keep(',
                 'FlakEngageCore.Resume(', 'if (best == null) g.LostGo = g.Target.Go;'):
        assert need in search, need
    assert 'ReachU(g, target.Pos,' in flak[flak.index('internal static void FocusRadar('):]
    assert 'FlakEngageCore.ResumeSeconds' in flak[flak.index('internal static void Control(Flak.Gun g'):]
    assert 'g.Err = Offset(t, mid, dist * MercAA.Calibration(g).InitialMil * 0.001f);' in flak
    assert 'bool ready = reach && g.Held >= reaction && error < 1.5f' in flak
    assert 'if (Vector3.Angle(dir, want) < 1f) dir = want;' in flak
    values = {
        'VELOCITY': bind(flak, 'MuzzleVelocity'), 'TURN': bind(flak, 'TraverseSpeed'),
        'ELEV': bind(flak, 'ElevationSpeed'), 'ACCEL': bind(flak, 'SlewAcceleration'),
        'PITCHMIN': bind(flak, 'PitchMin'), 'PITCHMAX': bind(flak, 'PitchMax'),
        'CEILING': bind(flak, 'Ceiling'), 'LAG': bind(flak, 'TrackingLag'), 'SPLASH': bind(flak, 'BurstRadius'),
        'MAXFUZE': bind(flak, 'MaxFuzeRange'), 'RELOAD': bind(flak, 'ReloadSeconds'),
        'RPM': bind(flak, 'RateOfFire'), 'CREWRPM': bind(flak, 'FullCrewRateOfFire'),
        'ROUNDS': bind(flak, 'Rounds'), 'DISPERSION': bind(flak, 'Dispersion'), 'HELI_HITS': bind(flak, 'HeliHits'),
        'SPEEDFACTOR': bind(npc, 'SpeedFactor'),
        'BOMBER_ALT': number(events, 'BomberAltitudeM'), 'BOMBER_KMH': number(events, 'BomberKmh'),
        'ESCORT_ALT': number(events, 'EscortAltitudeM'), 'ESCORT_KMH': number(events, 'EscortKmh'),
        'TRANSPORT_ALT': number(events, 'TransportAltitudeM'), 'TRANSPORT_KMH': number(events, 'TransportKmh'),
        'JUMP_SPREAD': number(events, 'JumpSpread'), 'ESCORT_STICK': number(events, 'EscortStick'),
        'BOMBER_STICK': '300',  # the log's Tu-95 "stick 300 u" at level 0
        'BOMBER_DELAY': number(retake, 'BomberDelay'), 'TRANSPORT_DELAY': number(retake, 'TransportDelay'),
        'HELI_DELAY': number(retake, 'HeliDelay'), 'HELI_STAGGER': number(retake, 'HeliStagger'),
        'HELI_ALT': bind(troops, 'CruiseHeight'), 'HELI_APPROACH': bind(troops, 'ApproachDistance'),
        'HELI_KMH': str(float(bind(troops, 'HeliSpeed')) * 3.6),
        'RAIDS': os.environ.get('FLAK_SIM_RAIDS', '300'), 'LOS': '0.10',
    }
    assert 'Rounds", 20' in flak and 'internal static bool Ready(Flak.Gun g) { return g.Ammo.CanShoot; }' in ammo
    # AirPicturePolicy calls the pure AirDefencePolicy rules; take them unchanged
    # from Revival.AirDefence.cs (the rest of that file needs Unity).
    defence = read('Revival.AirDefence.cs')
    start = defence.index('    internal static class AirDefencePolicy')
    policy = defence[start:defence.index('    // No Update: pilot identities', start)]
    generated = HARNESS + '\nnamespace NextDayRevival {\n' + policy + '}\n'
    for key, value in values.items():
        generated = generated.replace('@' + key + '@', value)
    assert '@' not in generated, re.findall(r'@\w+@', generated)
    OUT.mkdir(parents=True, exist_ok=True)
    harness = OUT / 'Harness.cs'
    harness.write_bytes(generated.encode('ascii'))
    csc = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework64/v3.5/csc.exe'
    exe = OUT / 'sim.exe'
    cores = ['Revival.FlakEngageCore.cs', 'Revival.MercAACore.cs', 'Revival.RadarClarityCore.cs',
             'Revival.AirPicturePolicy.cs', 'Revival.AirKillCore.cs', 'Revival.RetakeRaidsCore.cs',
             'Revival.FlakPositionsCore.cs']
    built = subprocess.run([str(csc), '/nologo', '/optimize+', '/warn:0', '/out:' + str(exe), str(harness)]
                           + [str(ROOT / c) for c in cores], capture_output=True)
    if built.returncode:
        print(built.stdout.decode('utf-8', 'replace'))
        return 1
    result = subprocess.run([str(exe)], capture_output=True)
    output = result.stdout.decode('utf-8', 'replace')
    (OUT / 'result.txt').write_bytes(output.encode('ascii', 'replace'))
    sys.stdout.write(output)
    return result.returncode


if __name__ == '__main__':
    raise SystemExit(main())
