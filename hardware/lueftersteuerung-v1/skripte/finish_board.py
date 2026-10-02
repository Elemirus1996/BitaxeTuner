# Leiterbahnen aus Freerouting übernehmen, Masseflächen (beide Lagen) anlegen und füllen, speichern.
import sys
import pcbnew

pcb, ses = sys.argv[1], sys.argv[2]
board = pcbnew.LoadBoard(pcb)
if not pcbnew.ImportSpecctraSES(board, ses):
    raise SystemExit("SES-Import fehlgeschlagen")

W, H = 115.0, 85.0
gnd = board.FindNet("GND")
for layer in (pcbnew.F_Cu, pcbnew.B_Cu):
    z = pcbnew.ZONE(board)
    z.SetLayer(layer)
    z.SetNetCode(gnd.GetNetCode())
    z.SetLocalClearance(pcbnew.FromMM(0.3))
    z.SetMinThickness(pcbnew.FromMM(0.25))
    z.SetPadConnection(pcbnew.ZONE_CONNECTION_THERMAL)
    z.SetThermalReliefGap(pcbnew.FromMM(0.4))
    z.SetThermalReliefSpokeWidth(pcbnew.FromMM(0.5))
    z.SetIsFilled(False)
    o = z.Outline()
    o.NewOutline()
    m = 1.0   # 1 mm Abstand zum Rand (auch an den abgerundeten Ecken)
    for x, y in ((m, m), (W - m, m), (W - m, H - m), (m, H - m)):
        o.Append(pcbnew.FromMM(x), pcbnew.FromMM(y))
    board.Add(z)

pcbnew.ZONE_FILLER(board).Fill(board.Zones())
pcbnew.SaveBoard(pcb, board)
print("ok", len(board.GetTracks()), "Bahnen/Vias")
