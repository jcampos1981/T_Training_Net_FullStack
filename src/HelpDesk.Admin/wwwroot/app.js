// Lo que el panel, que corre en el servidor, necesita preguntarle al navegador.
window.helpDesk = {
  timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,
};
