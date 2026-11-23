// The default shell's entry module — injected into an app's wwwroot root only when the app authored no
// index.html of its own. It hands the default canvas selector to the shared boot helper. An embedder who owns
// their own page writes their own main.js calling runKaldra({ canvas: '#their-canvas' }) instead of this one.
import { runKaldra } from './_content/BinaryVibrance.Kaldra.Hosting.Web/kaldra-boot.js'

await runKaldra({ canvas: '#kaldra-canvas' });
