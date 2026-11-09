import { dotnet } from './_framework/dotnet.js'
const rt = await dotnet.create();
await rt.runMain();
