-- The rare and magic share of scattered monster packs, the least number of rare packs an area with scattered packs
-- keeps, and the multiplier on the items a monster drops.
INSERT OR REPLACE INTO system_settings (key, value) VALUES
('monster_rare_pack_chance', 6.0),
('monster_magic_pack_chance', 12.0),
('monster_min_rare_packs', 2.0),
('item_quantity_multiplier', 1.0);
