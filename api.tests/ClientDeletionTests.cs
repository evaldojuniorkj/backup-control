using Xunit;

public class ClientDeletionTests
{
    [Theory]
    [InlineData("/","client")]
    [InlineData("/backup/../other","client")]
    [InlineData("/backup","../client")]
    [InlineData("/backup","client/other")]
    public void Unsafe_targets_rejected(string root,string slug)=>Assert.Throws<ArgumentException>(()=>ClientDeletionPolicy.Path(root,slug));

    [Fact]
    public async Task Traversal_deletes_only_exact_tree()
    {
        var fs=new FakeFtp();
        fs.Entries["/"]=[new("backup",DeletionEntryKind.Directory)];
        fs.Entries["/backup"]=[new("client",DeletionEntryKind.Directory),new("sibling",DeletionEntryKind.Directory)];
        fs.Entries["/backup/client"]=[new("sub",DeletionEntryKind.Directory),new("file.zip",DeletionEntryKind.File)];
        fs.Entries["/backup/client/sub"]=[new("data.zip",DeletionEntryKind.File)];
        var counts=await ClientDeletionPolicy.Remove(fs,"/backup","client",default);
        Assert.Equal(2,counts.Files);Assert.Equal(2,counts.Directories);
        Assert.Equal(new[]{"/backup/client/file.zip","/backup/client/sub/data.zip","/backup/client/sub","/backup/client"}.OrderBy(x=>x),fs.Deleted.OrderBy(x=>x));
        Assert.DoesNotContain("/backup",fs.Deleted);Assert.DoesNotContain("/backup/sibling",fs.Deleted);
    }
    [Theory]
    [InlineData("/")]
    [InlineData("/backup")]
    [InlineData("/backup/client")]
    public async Task Links_rejected_before_any_deletion(string level)
    {
        var fs=new FakeFtp();
        fs.Entries["/"]=[new("backup",DeletionEntryKind.Directory)];
        fs.Entries["/backup"]=[new("client",DeletionEntryKind.Directory)];
        fs.Entries["/backup/client"]=[new("data.zip",DeletionEntryKind.File),new("link",DeletionEntryKind.Link)];
        if(level=="/")fs.Entries[level]=[new("backup",DeletionEntryKind.Link)];
        if(level=="/backup")fs.Entries[level]=[new("client",DeletionEntryKind.Link)];
        await Assert.ThrowsAsync<IOException>(()=>ClientDeletionPolicy.Remove(fs,"/backup","client",default));
        Assert.Empty(fs.Deleted);
    }
    public sealed class FakeFtp : IDeletionFtp
    {
        public Dictionary<string,DeletionEntry[]> Entries=new();
        public List<string> Deleted=new();
        public Task<DeletionEntry[]> List(string path,CancellationToken ct)=>Task.FromResult(Entries[path]);
        public Task DeleteFile(string path,CancellationToken ct){Deleted.Add(path);Remove(path);return Task.CompletedTask;}
        public Task RemoveDirectory(string path,CancellationToken ct){Assert.Empty(Entries[path]);Deleted.Add(path);Remove(path);return Task.CompletedTask;}
        void Remove(string path){var pos=path.LastIndexOf('/');var parent=pos==0?"/":path[..pos];Entries[parent]=Entries[parent].Where(x=>x.Name!=path[(pos+1)..]).ToArray();}
    }
}
