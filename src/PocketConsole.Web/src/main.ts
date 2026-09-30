import { createApp } from "vue";
import App from "./App.vue";
import "./styles.css";
import "./overflow.css";
import "./login.css";

createApp(App).mount("#root");

if ("serviceWorker" in navigator) {
  window.addEventListener("load", () => navigator.serviceWorker.register("/sw.js", { updateViaCache: "none" }).then(registration => registration.update()));
}
