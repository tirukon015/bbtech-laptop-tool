-- Default test definitions (same as the desktop tool's built-in QcRequirements.Default()).
-- Admins change these through the API; the change reaches every workstation with the next config refresh.
INSERT INTO test_definitions (department_code, test_id, label, required, skip_allowed, sort_order) VALUES
    ('Hardware', 'Battery', 'Battery', true, false, 1),
    ('Hardware', 'Storage', 'Storage / SSD', true, false, 2),
    ('Hardware', 'Ram', 'RAM', true, false, 3),
    ('Hardware', 'Display', 'Display', true, false, 4),
    ('Hardware', 'Keyboard', 'Keyboard', true, false, 5),
    ('Hardware', 'Touchpad', 'Touchpad', true, false, 6),
    ('Hardware', 'Speaker', 'Sound', true, false, 7),
    ('Hardware', 'Headphone', 'Headphone', true, true, 8),
    ('Hardware', 'Microphone', 'Microphone', true, false, 9),
    ('Hardware', 'Camera', 'Camera', true, false, 10),
    ('Hardware', 'Usb', 'USB', true, false, 11),
    ('Hardware', 'WiFi', 'Wi-Fi', true, false, 12),
    ('Hardware', 'Bluetooth', 'Bluetooth', true, true, 13),
    ('Hardware', 'Mouse', 'Mouse', false, true, 14),
    ('Hardware', 'PhysicalInspection', 'Physical Inspection', true, false, 15),
    ('Hardware', 'OtherInspection', 'Other Inspection', true, false, 16),
    ('Software', 'SoftwareDrivers', 'Drivers', true, false, 1),
    ('Software', 'SoftwareDisplay', 'Display & Driver', true, false, 2),
    ('Software', 'SoftwareKeyboard', 'Keyboard Matrix', true, false, 3),
    ('Software', 'SoftwareMouse', 'Mouse Tracking', true, true, 4),
    ('Software', 'SoftwareScroll', 'Mouse Scroll Wheel', true, true, 5),
    ('Software', 'SoftwareHotkeys', 'System Hotkeys', true, false, 6),
    ('Software', 'SoftwareTouchpad', 'Touchpad & Gestures', true, false, 7),
    ('Software', 'SoftwareAudio', 'Audio Output', true, false, 8),
    ('Software', 'SoftwareMicrophone', 'Microphone Input', true, false, 9),
    ('Software', 'SoftwareCamera', 'Camera', true, false, 10),
    ('Software', 'SoftwareWindowsActivation', 'Windows Activation', true, false, 11),
    ('Software', 'SoftwareOfficeActivation', 'Microsoft Office Activation', false, false, 12),
    ('Software', 'SoftwareOfficeInstall', 'Microsoft Office Installation', false, false, 13),
    ('QC', 'Display', 'Display', true, false, 1),
    ('QC', 'Keyboard', 'Keyboard', true, false, 2),
    ('QC', 'Touchpad', 'Touchpad', true, false, 3),
    ('QC', 'Battery', 'Battery', true, false, 4),
    ('QC', 'Camera', 'Camera', true, false, 5),
    ('QC', 'Speaker', 'Sound', true, false, 6),
    ('QC', 'WiFi', 'Wi-Fi', true, false, 7),
    ('QC', 'Usb', 'USB', true, false, 8),
    ('QC', 'PhysicalInspection', 'Physical Inspection', true, false, 9),
    ('QC', 'OtherInspection', 'Other Inspection', true, false, 10);

-- Default approved package: Microsoft's online Office bootstrapper, verified by its Authenticode publisher.
INSERT INTO software_packages (package_id, name, version, required, detect_rule, approved, created_by)
VALUES ('office-default', 'Microsoft Office', 'Office 2024 / 365', false, 'office', true, 'migration');
INSERT INTO installers (package_id, source_mode, file_name, publisher, requires_elevation)
VALUES ('office-default', 0, 'OfficeSetup.exe', 'Microsoft Corporation', true);
