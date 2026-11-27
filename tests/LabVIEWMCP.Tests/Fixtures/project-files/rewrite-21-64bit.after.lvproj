<?xml version='1.0' encoding='UTF-8'?>
<Project Type="Project" LVVersion="21008000">
	<Property Name="NI.LV.All.SaveVersion" Type="Str">21.0</Property>
	<Property Name="NI.LV.All.SourceOnly" Type="Bool">true</Property>
	<Property Name="NI.Project.Description" Type="Str"></Property>
	<Item Name="My Computer" Type="My Computer">
		<Property Name="NI.SortType" Type="Int">3</Property>
		<Property Name="server.app.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="server.tcp.enabled" Type="Bool">false</Property>
		<Item Name="Helpers" Type="Folder">
			<Item Name="Scale.vi" Type="VI" URL="../Helpers/Scale.vi"/>
		</Item>
		<Item Name="Main.vi" Type="VI" URL="../Main.vi"/>
		<Item Name="Dependencies" Type="Dependencies"/>
		<Item Name="Build Specifications" Type="Build">
			<Item Name="App" Type="EXE">
				<Property Name="App_copyErrors" Type="Bool">true</Property>
				<Property Name="Bld_buildSpecName" Type="Str">App</Property>
			</Item>
			<Item Name="App Installer" Type="Installer">
				<Property Name="DistPart[4].flavorID" Type="Str"></Property>
				<Property Name="DistPart[4].productID" Type="Str"></Property>
				<Property Name="DistPart[4].productName" Type="Str">NI LabVIEW Run-Time Engine 2021</Property>
				<Property Name="DistPart[4].upgradeCode" Type="Str">{251E8668-4013-3671-857C-61D46B2AC827}</Property>
				<Property Name="DistPartCount" Type="Int">7</Property>
				<Property Name="InstSpecBitness" Type="Str">32-bit</Property>
				<Property Name="InstSpecVersion" Type="Str">21000000</Property>
			</Item>
		</Item>
	</Item>
</Project>
