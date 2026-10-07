CREATE TRIGGER guard_loss_journal BEFORE INSERT ON claimcore_witness.journal
FOR EACH ROW EXECUTE FUNCTION claimcore_witness.guard_loss_journal();
CREATE TRIGGER guard_loss_installation BEFORE UPDATE ON claimcore_witness.installation
FOR EACH ROW EXECUTE FUNCTION claimcore_witness.guard_loss_installation();
