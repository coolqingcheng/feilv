using System.Net;
using System.Text;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.QrSharing;

internal static class QrWebPage
{
    internal static string Render(string basePath, string relative, IReadOnlyList<SharedEntry> entries)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var rows = new StringBuilder();
        if (relative.Length > 0)
        {
            var parent = Path.GetDirectoryName(relative) ?? "";
            rows.Append("<a class='entry' href='").Append(basePath).Append("?path=").Append(Uri.EscapeDataString(parent)).Append("'><span>返回上级目录</span><b>↑</b></a>");
        }
        foreach (var entry in entries)
        {
            var link = basePath + (entry.IsDirectory ? "?path=" : "download?path=") + Uri.EscapeDataString(entry.RelativePath);
            rows.Append("<a class='entry' href='").Append(E(link)).Append("'").Append(entry.IsDirectory ? "" : " download").Append("><span>")
                .Append(E(entry.Name)).Append("<small>").Append(entry.IsDirectory ? "文件夹" : Size(entry.Size)).Append("</small></span><b>").Append(entry.IsDirectory ? "›" : "↓").Append("</b></a>");
        }
        if (entries.Count == 0) rows.Append("<p class='muted'>这个目录暂时没有文件</p>");
        return $$$"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>飞驴 · 文件分享</title>
            <style>:root{color-scheme:light dark;--bg:#f6f7f8;--card:#fff;--text:#202428;--muted:#67717b;--line:#e4e7e9;--accent:#008b57}*{box-sizing:border-box}body{font:15px system-ui,sans-serif;margin:0;background:var(--bg);color:var(--text)}main{max-width:700px;margin:auto;padding:28px 18px}h1{font-size:24px;margin:0 0 8px}h2{font-size:17px;margin:0 0 14px}.muted,small{color:var(--muted)}.card{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:20px;margin-top:20px}button,.pick{border:0;border-radius:10px;background:var(--accent);color:#fff;padding:12px 18px;font:inherit;cursor:pointer;display:inline-block}input{max-width:100%;display:block;margin:14px 0}button:disabled{opacity:.55}.entry{display:flex;justify-content:space-between;align-items:center;gap:16px;padding:16px 0;border-bottom:1px solid var(--line);color:inherit;text-decoration:none;overflow-wrap:anywhere}.entry:last-child{border:0}.entry span{min-width:0}small{display:block;font-size:12px;margin-top:5px}b{color:var(--accent)}progress{width:100%;height:10px;accent-color:var(--accent)}#status{white-space:pre-wrap;overflow-wrap:anywhere;font-size:13px}footer{font-size:12px;margin-top:20px;color:var(--muted)}@media(prefers-color-scheme:dark){:root{--bg:#242424;--card:#303030;--text:#f1f3f4;--muted:#b4bcc3;--line:#424242;--accent:#008b57}}</style></head><body><main>
            <h1>飞驴 · 文件分享</h1><div class="muted">手机与电脑连接同一个局域网，即可传文件。</div>
            <section class="card"><h2>上传到电脑</h2><input id="files" type="file" multiple><button id="upload">开始上传</button><progress id="progress" max="100" value="0" hidden></progress><p id="status" role="status"></p></section>
            <section class="card"><h2>下载文件</h2><p class="muted">{{{E(relative.Length == 0 ? "共享目录" : relative)}}}</p>{{{rows}}}<small>点击文件即可下载，点击文件夹继续浏览。</small></section>
            <footer>上传和下载目录由电脑端分别设置。电脑停止分享后，此链接失效。</footer></main>
            <script>const input=document.getElementById('files'),button=document.getElementById('upload'),bar=document.getElementById('progress'),status=document.getElementById('status');button.onclick=async()=>{const files=Array.from(input.files);if(!files.length){status.textContent='请先选择文件';return;}button.disabled=true;input.disabled=true;bar.hidden=false;const messages=[];for(const file of files){try{await new Promise((resolve,reject)=>{const xhr=new XMLHttpRequest();xhr.open('POST','{{{basePath}}}upload?name='+encodeURIComponent(file.name));xhr.setRequestHeader('Content-Type','application/octet-stream');xhr.upload.onprogress=e=>{if(e.lengthComputable)bar.value=e.loaded/e.total*100;status.textContent=messages.concat('正在上传：'+file.name+' '+Math.round(bar.value)+'%').join('\n');};xhr.onload=()=>{if(xhr.status===201){messages.push(xhr.responseText);resolve();}else reject(new Error(xhr.responseText||'上传失败'));};xhr.onerror=()=>reject(new Error('连接已中断，请检查电脑是否仍在分享'));xhr.send(file);});}catch(error){messages.push(file.name+'：'+error.message);}}status.textContent=messages.join('\n');button.disabled=false;input.disabled=false;bar.value=100;const refresh=document.createElement('button');refresh.textContent='刷新文件列表';refresh.onclick=()=>location.reload();status.append(document.createElement('br'),refresh);};</script></body></html>
            """;
    }

    private static string Size(long size) => size < 1024 ? size + " B" : size < 1024 * 1024 ? (size / 1024d).ToString("F1") + " KB" : size < 1024L * 1024 * 1024 ? (size / (1024d * 1024)).ToString("F1") + " MB" : (size / (1024d * 1024 * 1024)).ToString("F1") + " GB";
}
