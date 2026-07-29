# Mich Mapper 3.3 — Motore specifico Cerved

Questa versione introduce un parser dedicato ai dossier Cerved.

## Logica

- riconosce dossier impresa e dossier persona;
- ricostruisce le righe usando le coordinate delle parole nel PDF;
- ricerca i campi attraverso le etichette reali dei dossier Cerved;
- valida formalmente la Partita IVA;
- conserva pagina, evidenza, metodo e affidabilità per ogni dato;
- usa il nome del file solo come fallback segnalato;
- genera tre fogli Excel:
  - ANAGRAFICHE
  - EVIDENZE
  - TESTO_PER_PAGINA

## Campi della prima fase

- denominazione o nominativo;
- cognome e nome per dossier persona;
- Partita IVA;
- codice fiscale;
- attività economica;
- forma giuridica;
- situazione impresa;
- REA;
- data di costituzione.

Le sezioni soci, cariche, partecipazioni e bilancio verranno aggiunte dopo la
validazione di questa base sui dossier reali.
