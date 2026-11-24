# 1.0.4 — Primera versión pública

Esta es la primera versión pública del traductor de voz para reuniones en Windows más Azure.

- **Mi voz → reunión:** elige el perfil que traduce de español a inglés.
- **Reunión → mis auriculares:** elige el perfil que traduce de inglés a español.
- Cada selector admite Azure AI Speech o GPT Realtime Translate. Puedes usar los dos iguales o mezclarlos en cualquier orden. Para cambiar uno, solo debes detener su dirección. Para editar perfiles y credenciales, detén ambas.
- Conserva los perfiles, las credenciales cifradas, los dispositivos, el enrutamiento y las voces de Speech. La configuración anterior empieza usando el perfil previo en ambas direcciones. No hay cambio automático de proveedor si uno falla.

## Descargar y configurar

Extrae el ZIP completo en una carpeta nueva y abre `TraductorVozTeams.exe`. Requiere Windows 11 x64, .NET Desktop Runtime 10 x64 instalado por separado y Visual C++ Redistributable x64. No incluye credenciales, configuración personal ni controladores de audio.

La [guía de configuración y funcionamiento](https://github.com/eberd1977-blip/traductor-de-voz-para-reuniones-windows-azure/blob/main/README.md#inicio-rápido-104) explica los perfiles Speech y GPT, la verificación GPT sin archivos WAV, los dos cables virtuales, los ajustes de Teams/Meet/Zoom y cómo volver a una versión anterior.

## Validación y límites

Las pruebas locales cubren las cuatro combinaciones (Speech/Speech, Speech/GPT, GPT/Speech y GPT/GPT), persistencia, configuración anterior y bloqueo por dirección. No usaron Azure. Queda por comprobar el audio de las combinaciones mixtas con servicios reales y participantes, y no se publican conclusiones de latencia, calidad o factura para esas combinaciones.
