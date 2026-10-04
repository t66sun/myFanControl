namespace EcIdentityProbe;
internal static class SelfCheck
{
    internal static void Run()
    {
        var success=new FakeIo(0x5570);
        var result=new IdentityReader(success).Read();
        if(result.RawChipId!=0x5570 || result.Mapping1060!=0x80 || !result.SelectorsRestored || result.Errors.Count!=0 || !success.OriginalState)
            throw new InvalidOperationException("Identity/restore mismatch");
        var unknown=new FakeIo(0xffff);var unknownResult=new IdentityReader(unknown).Read();
        if(unknownResult.Mapping1060!=null || unknown.Addresses.Contains(0x1060) || !unknown.OriginalState)
            throw new InvalidOperationException("Unknown-chip gate failed");
        int failureCases=0;
        for(int fail=2;fail<=success.Operations;fail++)
            foreach(bool after in new[]{false,true})
            {
                var io=new FakeIo(0x5570,fail,after);var read=new IdentityReader(io).Read();
                if(read.Errors.Count==0)throw new InvalidOperationException("Failure disappeared");
                if(read.SelectorsRestored && !io.OriginalState)throw new InvalidOperationException("False restoration success");
                failureCases++;
            }
        Console.WriteLine($"Simulated selector protocol: normal and unknown-chip paths, {failureCases} injected failures passed. No driver/device access.");
    }
    private sealed class FakeIo(ushort id,int failureAt=0,bool failAfter=false):ISelectorIo
    {
        private byte selector=7,high=0xaa,low=0xbb;
        public int Operations {get;private set;}
        public HashSet<ushort> Addresses {get;}=[];
        public bool OriginalState=>selector==7 && high==0xaa && low==0xbb;
        private void Failure(bool after)
        {
            if(after==failAfter && Operations==failureAt)throw new IOException("Injected failure");
        }
        public byte Read(byte register)
        {
            Operations++;Failure(false);
            byte value;
            if(register==0x2e)value=selector;
            else if(register==0x2f && selector==0x11)value=high;
            else if(register==0x2f && selector==0x10)value=low;
            else if(register==0x2f && selector==0x12)
            {
                ushort address=(ushort)(high*256+low);Addresses.Add(address);
                value=address switch {0x2000=>(byte)(id>>8),0x2001=>(byte)id,0x1060=>0x80,_=>throw new InvalidOperationException("Unexpected address")};
            }
            else throw new InvalidOperationException("Unexpected read");
            Failure(true);return value;
        }
        public void Write(byte register,byte value)
        {
            Operations++;Failure(false);
            if(register==0x2e)selector=value;
            else if(register==0x2f && selector==0x11)high=value;
            else if(register==0x2f && selector==0x10)low=value;
            else throw new InvalidOperationException("Forbidden write to target data or another selector");
            Failure(true);
        }
    }
}
