// Review-time runtime pinning; the backend independently checks membership and
// every digest before native execution. This never changes the installed CLR.
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import assert from 'node:assert/strict';

export async function pinnedFramework(program,version=process.env.CONVEX_DOTNET_FRAMEWORK_VERSION||'10.0.12'){
  assert.match(version,/^\d{1,5}\.\d{1,5}\.\d{1,5}$/);
  const root=path.dirname(await fs.realpath(program)),artifacts=[];
  async function walk(directory){
    for(const entry of await fs.readdir(directory,{withFileTypes:true})){
      const file=path.join(directory,entry.name);
      assert.ok(!entry.isSymbolicLink(),'runtime closure must use regular files');
      if(entry.isDirectory())await walk(file);
      else {
        assert.ok(entry.isFile());assert.ok(artifacts.length<256);
        const bytes=await fs.readFile(file);assert.ok(bytes.length>0&&bytes.length<=64*1024*1024);
        artifacts.push({path:file,sha256:createHash('sha256').update(bytes).digest('hex')});
      }
    }
  }
  await walk(path.join(root,'shared/Microsoft.NETCore.App',version));await walk(path.join(root,'host/fxr'));
  return {version,artifacts:artifacts.sort((a,b)=>a.path.localeCompare(b.path))};
}
