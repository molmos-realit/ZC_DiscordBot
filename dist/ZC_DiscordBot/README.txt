ZC_DiscordBot v1.2.0 - Zona Cero
================================

FUNCION
Bot server-side para Discord. Responde !estado, !jugadores, !luna, !ranking,
!boss, !reinicio y !version; anuncia reinicios; edita un unico ranking de Luna
de Sangre; y publica eventos Boss cuando el mod BossEvents esta cargado.

CAMBIO v1.2.0
Compilado contra 7DTD V3.3.0. El bot es independiente: solo lee estado del juego (jugadores, dia,
maxima de jugadores) y no modifica ninguna logica del juego. El parche Harmony (postfix de
GameManager.Update) esta aislado con try/catch. Integraciones con BossEvents 4.x y BloodMoonRanking
siguen siendo opcionales por reflexion.

CAMBIO CLAVE v1.1.0
BossEvents y BloodMoonRanking ahora son integraciones opcionales por reflexion.
El DiscordBot ya no referencia esas DLL de forma obligatoria, asi que debe cargar
aunque esos mods falten, se hayan retirado o carguen despues. Si los detecta,
activa ranking/eventos; si no los detecta, responde como no disponible.

REQUISITOS
- 7 Days to Die Dedicated Server V3.3.0 (compilado y verificado contra 3.3.0).
- HTTPS saliente a discord.com:443 permitido por el hosting.
- Token de bot de Discord y channelId configurados en Config/DiscordBot.xml.

CONFIGURACION
Edite Mods/ZC_DiscordBot/Config/DiscordBot.xml:
- enabled="true"
- botToken="..."
- channelId="..."
- Configure horarios y zona horaria de reinicio.

El token se utiliza solo como cabecera de autorizacion y nunca se escribe en logs.
Proteja Config/DiscordBot.xml. Si el token se filtra, regenere el token en Discord.

INSTALACION
1. Detenga el servidor.
2. Suba la carpeta ZC_DiscordBot directamente dentro de Mods.
3. Si ya tiene un DiscordBot.xml real en el servidor, conserve ese archivo o copie
   sus valores al nuevo XML antes de iniciar.
4. Inicie el servidor y busque "[ZC_DiscordBot] v1.2.0 cargado" en el log.
5. Pruebe !version y !estado en el canal configurado.

NOTAS
- !version muestra si BossEvents y BloodMoonRanking fueron detectados.
- !jugadores muestra nombres, nunca IP, EOS ID ni Steam ID.
- No existe comando para ejecutar reinicio; !reinicio es informativo.
DIAGNOSTICO v1.1.1
Al recibir comandos, el log muestra 'Comando recibido' y 'Procesando comando normalizado'.
