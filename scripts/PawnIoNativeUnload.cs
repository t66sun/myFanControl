using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
public static class PawnIoNativeUnload {
 [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low;public int High; }
 [StructLayout(LayoutKind.Sequential)] struct Privilege { public uint Count;public Luid Id;public uint Attributes; }
 [StructLayout(LayoutKind.Sequential)] struct UString { public ushort Length;public ushort MaximumLength;public IntPtr Buffer; }
 [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool LookupPrivilegeValue(string system,string name,out Luid luid);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token,bool disable,ref Privilege state,uint length,out Privilege previous,out uint required);
 [DllImport("ntdll.dll")] static extern int NtUnloadDriver(ref UString name);
 public static int Unload() {
   IntPtr token; if(!OpenProcessToken(GetCurrentProcess(),0x28,out token))throw new Win32Exception();
   IntPtr memory=IntPtr.Zero;Privilege previous=new Privilege();bool adjusted=false;
   try {
     Luid id;if(!LookupPrivilegeValue(null,"SeLoadDriverPrivilege",out id))throw new Win32Exception();
     var state=new Privilege{Count=1,Id=id,Attributes=2};uint required;
     if(!AdjustTokenPrivileges(token,false,ref state,(uint)Marshal.SizeOf(typeof(Privilege)),out previous,out required))throw new Win32Exception();
     if(Marshal.GetLastWin32Error()==1300)throw new Win32Exception(1300);
     adjusted=true;const string path=@"\Registry\Machine\System\CurrentControlSet\Services\PawnIO";
     memory=Marshal.StringToHGlobalUni(path);var name=new UString{Length=(ushort)(path.Length*2),MaximumLength=(ushort)((path.Length+1)*2),Buffer=memory};
     return NtUnloadDriver(ref name);
   } finally {
     if(memory!=IntPtr.Zero)Marshal.FreeHGlobal(memory);
     if(adjusted){Privilege discarded;uint required;AdjustTokenPrivileges(token,false,ref previous,(uint)Marshal.SizeOf(typeof(Privilege)),out discarded,out required);}
     CloseHandle(token);
   }
 }
}
