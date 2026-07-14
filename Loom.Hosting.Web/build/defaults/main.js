// The default shell's entry module — injected into an app's wwwroot root only when the app authored no
// index.html of its own. It hands the default canvas selector to the shared boot helper. An embedder who owns
// their own page writes their own main.js calling runLoom({ canvas: '#their-canvas' }) instead of this one.
import { runLoom } from './_content/Loom.Hosting.Web/loom-boot.js'

await runLoom({ canvas: '#loom-canvas' });
